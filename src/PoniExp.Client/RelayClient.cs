using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Client;

/// <summary>
/// One machine's connection to the router. It proves its key on every (re)connect, so the router always
/// knows who is on the other end; incoming envelopes are verified against the sender's public key here
/// too, so a compromised router can drop or delay messages but not forge them.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    private readonly MachineIdentity _identity;
    private readonly HubConnection _connection;
    private readonly TimeProvider _time;
    private readonly Lock _seenLock = new();
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _seenOrder = new();
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private readonly IReadOnlyList<TimeSpan> _reconnectDelays;
    private readonly CancellationTokenSource _disposing = new();
    private Dictionary<string, MachineInfo> _machines = [];

    public RelayClient(MachineIdentity identity, RelayClientOptions options, TimeProvider? time = null)
    {
        _identity = identity;
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
            .Build();

        // Handlers only enqueue: awaiting a hub call inside an On handler would block the receive loop that
        // completes it. One worker keeps the order the router sent things in.
        _connection.On<Envelope>(HubMethods.Receive, envelope => Post(() => OnReceiveAsync(envelope)));
        _connection.On<PresenceChange>(HubMethods.PresenceChanged, change => Post(async () =>
        {
            if (_machines.TryGetValue(change.MachineId, out var known))
            {
                _machines = new Dictionary<string, MachineInfo>(_machines)
                {
                    [change.MachineId] = known with { Online = change.Online, LastSeen = change.At },
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

    /// <summary>A verified envelope from an approved machine. The client acks it after this returns.</summary>
    public event Func<ReceivedMessage, Task>? MessageReceived;

    public event Action<PresenceChange>? PresenceChanged;

    public event Action<MembershipStatus>? MembershipChanged;

    /// <summary>True when (re)connected and authenticated; false (with the error) when the link drops.</summary>
    public event Action<bool, Exception?>? ConnectionChanged;

    /// <summary>Envelopes that failed verification here (bad signature, unknown sender). Should never fire.</summary>
    public event Action<Envelope, string>? MessageRejected;

    public string MachineId => _identity.MachineId;

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
        var request = Signatures.SignJoin(_identity, name, description, _time.GetUtcNow());
        var result = await _connection.InvokeAsync<AuthResult>(HubMethods.RequestJoin, request, cancellationToken);
        Status = result.Status;
        Name = result.Name;
        return result.Status;
    }

    public string JoinCode => KeyFingerprint.JoinCodeOf(_identity.PublicKey);

    /// <summary>Signs and sends an envelope to a machine (by id or name).</summary>
    public async Task<SendResult> SendAsync(string to, string kind, string body, CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(to, cancellationToken);
        var envelope = Signatures.SignEnvelope(_identity, target.Id, kind, body, _time.GetUtcNow());
        return await _connection.InvokeAsync<SendResult>(HubMethods.Send, envelope, cancellationToken);
    }

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
        var challenge = await _connection.InvokeAsync<string>(HubMethods.GetChallenge, cancellationToken);
        var signature = _identity.Sign(Signatures.AuthPayload(challenge, _identity.PublicKey));
        var result = await _connection.InvokeAsync<AuthResult>(HubMethods.Authenticate, _identity.PublicKey, signature, cancellationToken);
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

    private async Task OnReceiveAsync(Envelope envelope)
    {
        if (envelope.To != _identity.MachineId)
        {
            MessageRejected?.Invoke(envelope, "addressed to another machine");
            return;
        }

        if (!_machines.TryGetValue(envelope.From, out var sender))
        {
            await RefreshMachinesAsync();
            _machines.TryGetValue(envelope.From, out sender);
        }

        if (sender is null || !Signatures.VerifyEnvelope(envelope, sender.PublicKey))
        {
            MessageRejected?.Invoke(envelope, sender is null ? "unknown sender" : "bad signature");
            return; // not acked: the router keeps it until it expires, for inspection
        }

        if (MarkSeen(envelope.Id) && MessageReceived is { } handler)
        {
            await handler(new ReceivedMessage(envelope, sender.Name));
        }

        await _connection.InvokeAsync(HubMethods.Ack, envelope.Id);
    }

    private bool MarkSeen(Guid id)
    {
        lock (_seenLock)
        {
            if (!_seen.Add(id))
            {
                return false;
            }

            _seenOrder.Enqueue(id);
            if (_seenOrder.Count > 10_000)
            {
                _seen.Remove(_seenOrder.Dequeue());
            }

            return true;
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

public sealed record ReceivedMessage(Envelope Envelope, string FromName);
