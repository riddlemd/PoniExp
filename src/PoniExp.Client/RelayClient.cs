using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Client;

/// <summary>
/// One machine's connection to the router. It says which protocol version it speaks and proves its key on
/// every (re)connect, so the router always knows who is on the other end. Incoming envelopes are checked
/// here too (signature, expiry by this machine's own clock, duplicates), so a compromised router can drop
/// or delay messages but not forge, replay or stretch them.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    private readonly MachineKeys _keys;
    private readonly Lock _verifiedLock = new();
    private readonly Dictionary<string, string> _verifiedKeys = [];
    private readonly HubConnection _connection;
    private readonly TimeProvider _time;
    private readonly ClientStore _store;
    private readonly IOptionsMonitor<MessagingOptions> _messaging;
    private readonly bool _confirm;
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private readonly IReadOnlyList<TimeSpan> _reconnectDelays;
    private readonly CancellationTokenSource _disposing = new();
    private Dictionary<string, MachineInfo> _machines = [];

    /// <exception cref="OptionsValidationException">The messaging settings are invalid.</exception>
    public RelayClient(MachineKeys keys, RelayClientOptions options, ClientStore store,
        IOptionsMonitor<MessagingOptions> messaging, TimeProvider? time = null)
    {
        _ = messaging.CurrentValue; // validates: bad settings stop the client here, not on the first message
        _keys = keys;
        _store = store;
        _messaging = messaging;
        _confirm = options.ConfirmMessages;
        _time = time ?? TimeProvider.System;
        _reconnectDelays = options.ReconnectDelays;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(options.RouterUrl, HubMethods.Path), http =>
            {
                http.Transports = HttpTransportType.WebSockets;
                http.SkipNegotiation = true;
                if (options.HttpMessageHandlerFactory is not null)
                {
                    http.HttpMessageHandlerFactory = options.HttpMessageHandlerFactory;
                    http.Transports = HttpTransportType.LongPolling;
                    http.SkipNegotiation = false;
                }
                else if (options.PinnedCertificateSha256 is { } pin)
                {
                    http.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler client)
                        {
                            client.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && Matches(cert, pin);
                        }

                        return handler;
                    };
                    http.WebSocketConfiguration = socket =>
                        socket.RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Matches(cert, pin);
                }
            })
            .WithAutomaticReconnect(new ForeverRetry(options.ReconnectDelays))
            .AddJsonProtocol(json => WireProtocol.Configure(json.PayloadSerializerOptions))
            .Build();

        // Handlers only enqueue: awaiting a hub call inside an On handler would block the receive loop that
        // completes it. One worker keeps the order the router sent things in.
        _connection.On<Delivery>(HubMethods.Receive, delivery => Post(() => OnReceiveAsync(delivery)));
        _connection.On<PresenceChange>(HubMethods.PresenceChanged, change => Post(async () =>
        {
            if (_machines.TryGetValue(change.MachineId, out var known))
            {
                _machines = new Dictionary<string, MachineInfo>(_machines)
                {
                    [change.MachineId] = known with { Online = change.Online, LastSeenOn = change.OccurredOn },
                };
            }
            else
            {
                await RefreshMachinesAsync(); // a machine approved since we last asked
            }

            PresenceChanged?.Invoke(change);
        }));
        _connection.On<MembershipStatus>(HubMethods.MembershipChanged, status => Post(() => OnMembershipChangedAsync(status)));
        _connection.Reconnected += _ =>
        {
            Post(OnReconnectedAsync);
            return Task.CompletedTask;
        };
        _connection.Reconnecting += error =>
        {
            ConnectionChanged?.Invoke(false, error);
            return Task.CompletedTask;
        };
        // The built-in reconnect covers dropped links; a close (the router restarting, or cutting us off)
        // ends it, so restart here unless this machine was rejected or removed.
        _connection.Closed += error =>
        {
            ConnectionChanged?.Invoke(false, error);
            if (!_disposing.IsCancellationRequested && Status is MembershipStatus.Approved or MembershipStatus.Pending)
            {
                Post(RestartAsync);
            }

            return Task.CompletedTask;
        };
        _worker = Task.Run(RunWorkerAsync);
    }

    /// <summary>
    /// A verified, stored message from an approved machine, raised once per message: duplicates are dropped
    /// before this. The message is already confirmed to the router.
    /// </summary>
    public event Func<ReceivedMessage, Task>? MessageReceived;

    public event Action<PresenceChange>? PresenceChanged;

    public event Action<MembershipStatus>? MembershipChanged;

    /// <summary>True when (re)connected and authenticated; false (with the error) when the link drops.</summary>
    public event Action<bool, Exception?>? ConnectionChanged;

    /// <summary>
    /// Envelopes refused here, with why. Not verified (bad signature, unknown sender, another machine's): left
    /// on the router. Verified but expired, from the future or malformed: confirmed and dropped, and counted.
    /// </summary>
    public event Action<Envelope, string>? MessageRejected;

    public ClientStore Store => _store;

    public string MachineId => _keys.MachineId;

    public MembershipStatus Status { get; private set; } = MembershipStatus.Unknown;

    public string? Name { get; private set; }

    public IReadOnlyCollection<MachineInfo> Machines => _machines.Values;

    /// <summary>Connects and authenticates. Returns where this machine stands with the router.</summary>
    public async Task<MembershipStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connection.StartAsync(cancellationToken);
        return await AuthenticateAsync(cancellationToken);
    }

    /// <summary>Asks to join. The router shows the owner a join code; compare it with <see cref="JoinCode"/>.</summary>
    public async Task<MembershipStatus> RequestJoinAsync(string name, MachineDescription description, CancellationToken cancellationToken = default)
    {
        var request = Signatures.SignJoin(_keys, name, description, _time.GetUtcNow());
        var result = await _connection.InvokeAsync<AuthResult>(HubMethods.RequestJoin, request, cancellationToken);
        Status = result.Status;
        Name = result.Name;
        return result.Status;
    }

    public string JoinCode => KeyFingerprint.JoinCodeOf(_keys.PublicKey);

    /// <summary>Signs and sends a message to <c>machine</c> or <c>machine/endpoint</c> (machine by id or name).</summary>
    public async Task<SentMessage> SendAsync(string to, OutgoingMessage message, CancellationToken cancellationToken = default) =>
        (await SendAsync([to], message, cancellationToken))[0];

    /// <summary>
    /// Sends one separate message to each recipient: its own id, sequence number and conversation. No message
    /// names another recipient. Conversation, reply-to and operation ids therefore only make sense for one.
    /// </summary>
    public async Task<IReadOnlyList<SentMessage>> SendAsync(IReadOnlyCollection<string> to, OutgoingMessage message,
        CancellationToken cancellationToken = default)
    {
        if (to.Count == 0)
        {
            throw new ArgumentException("Name at least one recipient.", nameof(to));
        }

        if (to.Count > 1 && (message.ConversationId is not null || message.ReplyTo is not null || message.OperationId is not null))
        {
            throw new ArgumentException("A message has one recipient: conversation, reply-to and operation ids are for a single one.", nameof(message));
        }

        var targets = new List<(MachineInfo Machine, string Endpoint)>();
        foreach (var address in to.Select(Address.Parse).ToList()) // all parsed, resolved and key-checked before anything is sent
        {
            var machine = await ResolveAsync(address.Machine, cancellationToken);
            VerifiedEncryptionKey(machine);
            targets.Add((machine, address.Endpoint));
        }

        var sent = new List<SentMessage>();
        foreach (var (machine, endpoint) in targets)
        {
            sent.Add(await SendOneAsync(machine, endpoint, message, cancellationToken));
        }

        return sent;
    }

    /// <summary>Replies to <paramref name="original"/>: to its sender's endpoint, in its conversation, from the endpoint it was sent to.</summary>
    public async Task<SentMessage> ReplyAsync(ReceivedMessage original, OutgoingMessage reply, CancellationToken cancellationToken = default) =>
        await SendOneAsync(await ResolveAsync(original.Envelope.FromMachine, cancellationToken), original.Content.FromEndpoint, reply with
        {
            FromEndpoint = reply.FromEndpoint ?? original.Content.ToEndpoint,
            ConversationId = original.Content.ConversationId,
            ReplyTo = original.Envelope.Id,
        }, cancellationToken);

    /// <summary>A stored message, decrypted: sent by this machine or received by it.</summary>
    public async Task<MessageContent?> ReadAsync(string fromMachine, Guid id) =>
        await _store.GetMessageAsync(fromMachine, id) is { Envelope: { } envelope } ? MessageCrypto.Open(envelope, _keys.MachineId, _keys.Encryption) : null;

    public async Task<IReadOnlyList<MachineInfo>> RefreshMachinesAsync(CancellationToken cancellationToken = default)
    {
        var machines = await _connection.InvokeAsync<List<MachineInfo>>(HubMethods.GetMachines, cancellationToken);
        _machines = machines.ToDictionary(m => m.Id);
        return machines;
    }

    /// <summary>Raised when queued work (a message handler, a refresh) throws. The client carries on.</summary>
    public event Action<Exception>? BackgroundError;

    public async ValueTask DisposeAsync()
    {
        await _disposing.CancelAsync();
        await _connection.DisposeAsync();
        _work.Writer.TryComplete();
        await _worker;
    }

    private async Task RestartAsync()
    {
        for (var attempt = 0; !_disposing.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(_reconnectDelays[Math.Min(attempt, _reconnectDelays.Count - 1)], _time, _disposing.Token);
                if (_connection.State == HubConnectionState.Disconnected)
                {
                    await _connection.StartAsync(_disposing.Token);
                }

                await AuthenticateAsync(_disposing.Token);
                return;
            }
            catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                ConnectionChanged?.Invoke(false, e);
            }
        }
    }

    /// <summary>
    /// The machine's encryption key, after checking that its own identity key signed it. The router hands
    /// keys out, so without this check it could hand out its own and read everything.
    /// </summary>
    private string VerifiedEncryptionKey(MachineInfo machine)
    {
        lock (_verifiedLock)
        {
            if (_verifiedKeys.TryGetValue(machine.Id, out var known) && known == machine.EncryptionKey)
            {
                return known;
            }
        }

        if (KeyFingerprint.MachineIdOf(machine.PublicKey) != machine.Id
            || !PublicKeys.VerifyEncryptionKey(machine.PublicKey, machine.EncryptionKey, machine.EncryptionKeySignature))
        {
            throw new InvalidOperationException(
                $"The router's encryption key for {machine.Name} isn't signed by {machine.Name}'s own key; refusing to encrypt to it.");
        }

        lock (_verifiedLock)
        {
            _verifiedKeys[machine.Id] = machine.EncryptionKey;
        }

        return machine.EncryptionKey;
    }

    private async Task<SentMessage> SendOneAsync(MachineInfo target, string toEndpoint, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var toMachine = target.Id;
        var recipientKey = VerifiedEncryptionKey(target);
        var settings = _messaging.CurrentValue;
        var lifetime = message.ExpiresIn ?? settings.DefaultExpiry;
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException("A message must live for some time.", nameof(message));
        }

        if (lifetime > settings.MaxExpiry)
        {
            lifetime = settings.MaxExpiry;
        }

        var now = _time.GetUtcNow();
        var id = Guid.NewGuid();
        var content = new MessageContent(
            message.FromEndpoint ?? Address.DefaultEndpoint,
            toEndpoint,
            message.ConversationId ?? Guid.NewGuid(),
            message.ReplyTo,
            message.OperationId ?? id,
            message.Status,
            string.IsNullOrEmpty(message.Subject) ? null : message.Subject,
            MessageType.Message,
            message.ContentType,
            message.Metadata ?? new Dictionary<string, string>(),
            message.Body,
            now);
        if (MessageRules.CheckContent(content) is { } problem)
        {
            throw new ArgumentException(problem, nameof(message));
        }

        // Stored, with its sequence number, before it leaves.
        var envelope = await _store.ReserveOutgoingAsync(toMachine, content,
            sequence => Signatures.SealEnvelope(_keys, id, toMachine, recipientKey, sequence, now + lifetime, content), now);
        SendResult result;
        try
        {
            result = await _connection.InvokeAsync<SendResult>(HubMethods.Send, envelope, cancellationToken);
        }
        catch (HubException)
        {
            await _store.ForgetOutgoingAsync(_keys.MachineId, id);
            throw;
        }

        return new SentMessage(envelope.Id, toMachine, toEndpoint, envelope.Sequence, envelope.ExpiresOn, result.State);
    }

    private void Post(Func<Task> work) => _work.Writer.TryWrite(work);

    private async Task RunWorkerAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception e)
            {
                BackgroundError?.Invoke(e);
            }
        }
    }

    private async Task<MachineInfo> ResolveAsync(string to, CancellationToken cancellationToken)
    {
        var match = Find(to);
        if (match is null)
        {
            await RefreshMachinesAsync(cancellationToken);
            match = Find(to);
        }

        return match ?? throw new InvalidOperationException($"No approved machine is called '{to}'.");

        MachineInfo? Find(string key) =>
            _machines.GetValueOrDefault(key) ?? _machines.Values.SingleOrDefault(m => m.Name == key);
    }

    private async Task<MembershipStatus> AuthenticateAsync(CancellationToken cancellationToken)
    {
        await _connection.InvokeAsync<HelloResult>(HubMethods.Hello, WireProtocol.Version, cancellationToken);
        var challenge = await _connection.InvokeAsync<string>(HubMethods.GetChallenge, cancellationToken);
        var signature = _keys.Sign(Signatures.AuthPayload(challenge, _keys.PublicKey));
        var result = await _connection.InvokeAsync<AuthResult>(HubMethods.Authenticate, _keys.PublicKey, signature, cancellationToken);
        Status = result.Status;
        Name = result.Name;
        if (result.Status == MembershipStatus.Approved)
        {
            await RefreshMachinesAsync(cancellationToken);
            ConnectionChanged?.Invoke(true, null);
        }

        return result.Status;
    }

    private async Task OnReconnectedAsync()
    {
        try
        {
            await AuthenticateAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            ConnectionChanged?.Invoke(false, e);
        }
    }

    private async Task OnMembershipChangedAsync(MembershipStatus status)
    {
        Status = status;
        MembershipChanged?.Invoke(status);
        if (status == MembershipStatus.Approved)
        {
            await AuthenticateAsync(CancellationToken.None); // join the members' groups on this connection
        }
    }

    private async Task OnReceiveAsync(Delivery delivery)
    {
        var envelope = delivery.Envelope;
        if (envelope?.Ciphertext is null || envelope.ToMachine != _keys.MachineId)
        {
            MessageRejected?.Invoke(envelope!, "addressed to another machine");
            return;
        }

        if (!_machines.TryGetValue(envelope.FromMachine, out var sender))
        {
            await RefreshMachinesAsync();
            _machines.TryGetValue(envelope.FromMachine, out sender);
        }

        if (sender is null || !Signatures.VerifyEnvelope(envelope, sender.PublicKey))
        {
            MessageRejected?.Invoke(envelope, sender is null ? "unknown sender" : "bad signature");
            return; // not confirmed: the router keeps it until it expires, for inspection
        }

        var settings = _messaging.CurrentValue;
        var now = _time.GetUtcNow();
        MessageContent content;
        try
        {
            content = MessageCrypto.Open(envelope, _keys.MachineId, _keys.Encryption);
        }
        catch (CryptographicException e)
        {
            await _store.CountAsync(envelope.FromMachine, ClientMetrics.Undecryptable, now);
            MessageRejected?.Invoke(envelope, $"can't decrypt: {e.Message}");
            await ConfirmAsync(envelope.Id); // signed by its sender, so it will never decrypt: drop it
            return;
        }

        // This machine's own clock decides, not the router's: a router can't stretch or replay messages.
        var expiresOn = envelope.ExpiresOn < content.CreatedOn + settings.MaxExpiry ? envelope.ExpiresOn : content.CreatedOn + settings.MaxExpiry;
        var refusal = MessageRules.CheckContent(content) is { } problem ? (ClientMetrics.Invalid, problem)
            : content.CreatedOn > now + settings.ClockSkew ? (ClientMetrics.FromTheFuture, "created in the future; check the sender's clock")
            : now > expiresOn + settings.ClockSkew ? (ClientMetrics.Expired, "expired")
            : default((string Metric, string Reason)?);

        var stored = false;
        if (refusal is { } r)
        {
            await _store.CountAsync(envelope.FromMachine, r.Metric, now);
            MessageRejected?.Invoke(envelope, r.Reason);
        }
        else
        {
            stored = await _store.StoreIncomingAsync(envelope, content, delivery.RelayedOn, expiresOn, _keys.MachineId, now);
        }

        // Stored (or refused for good): the router can drop it. A duplicate is confirmed again and dropped quietly.
        await ConfirmAsync(envelope.Id);

        if (stored && MessageReceived is { } handler)
        {
            await handler(new ReceivedMessage(envelope, content, sender.Name, delivery.RelayedOn));
        }
    }

    private async Task ConfirmAsync(Guid id)
    {
        if (!_confirm)
        {
            return;
        }

        try
        {
            await _connection.InvokeAsync(HubMethods.Confirm, id);
        }
        catch (Exception e)
        {
            BackgroundError?.Invoke(e); // it comes back on reconnect and is then a duplicate
        }
    }

    private static bool Matches(X509Certificate cert, string pin) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(cert.GetRawCertData()),
            Convert.FromHexString(pin.Replace(":", string.Empty, StringComparison.Ordinal)));

    private sealed class ForeverRetry(IReadOnlyList<TimeSpan> delays) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            delays[(int)Math.Min(retryContext.PreviousRetryCount, delays.Count - 1)];
    }
}

/// <summary>
/// A stored incoming message: its envelope (as signed and encrypted) and its decrypted content.
/// <see cref="RelayedOn"/> is when the router received it (its word, unsigned).
/// </summary>
public sealed record ReceivedMessage(Envelope Envelope, MessageContent Content, string FromName, DateTimeOffset RelayedOn)
{
    /// <summary>The sender as <c>name/endpoint</c>.</summary>
    public Address From => new(FromName, Content.FromEndpoint);
}

/// <summary>
/// What to send. Unset ids are generated: a new conversation, and the operation id defaults to the message
/// id. <see cref="ExpiresIn"/> defaults to the configured default expiry and is cut to the maximum.
/// </summary>
public sealed record OutgoingMessage(string Body)
{
    public string? FromEndpoint { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? ReplyTo { get; init; }

    public Guid? OperationId { get; init; }

    /// <summary>For replies: the outcome of the work. Null is the success path.</summary>
    public ReplyStatus? Status { get; init; }

    public string? Subject { get; init; }

    public string ContentType { get; init; } = "text/plain";

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public TimeSpan? ExpiresIn { get; init; }
}

public sealed record SentMessage(Guid Id, string ToMachine, string ToEndpoint, long Sequence, DateTimeOffset ExpiresOn, DeliveryState State);
