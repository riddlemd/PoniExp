using PoniExp.Core.Files;
using System.Net.Http.Json;
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
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private volatile bool _authenticated;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _unconfirmed = new();
    private Dictionary<string, MachineInfo> _machines = [];
    private readonly Func<HttpMessageHandler, HttpMessageHandler>? _handlerFactory;
    private readonly string? _pin;

    /// <exception cref="OptionsValidationException">The messaging settings are invalid.</exception>
    public RelayClient(MachineKeys keys, RelayClientOptions options, ClientStore store,
        IOptionsMonitor<MessagingOptions> messaging, TimeProvider? time = null)
    {
        _ = messaging.CurrentValue; // validates: bad settings stop the client here, not on the first message
        _keys = keys;
        _store = store;
        RouterUrl = options.RouterUrl;
        _messaging = messaging;
        _confirm = options.ConfirmMessages;
        _time = time ?? TimeProvider.System;
        _reconnectDelays = options.ReconnectDelays;
        _handlerFactory = options.HttpMessageHandlerFactory;
        _pin = options.PinnedCertificateSha256;
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
        _connection.On<RoomState>(HubMethods.RoomChanged, room => Post(() =>
        {
            RoomChanged?.Invoke(room); // on the worker, so before any room message the router sends after it
            return Task.CompletedTask;
        }));
        _connection.Reconnected += _ =>
        {
            Post(OnReconnectedAsync);
            return Task.CompletedTask;
        };
        _connection.Reconnecting += error =>
        {
            _authenticated = false;
            ConnectionChanged?.Invoke(false, error);
            return Task.CompletedTask;
        };
        // The built-in reconnect covers dropped links; a close (the router restarting, or cutting us off)
        // ends it, so restart here unless this machine was rejected or removed.
        _connection.Closed += error =>
        {
            _authenticated = false;
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

    /// <summary>A room this machine is involved in changed or collapsed (the log is unchecked: the caller replays it).</summary>
    public event Action<RoomState>? RoomChanged;

    /// <summary>True when (re)connected and authenticated; false (with the error) when the link drops.</summary>
    public event Action<bool, Exception?>? ConnectionChanged;

    /// <summary>
    /// Envelopes refused here, with why. Not verified (bad signature, unknown sender, another machine's): left
    /// on the router. Verified but expired, from the future or malformed: confirmed and dropped, and counted.
    /// </summary>
    public event Action<Envelope, string>? MessageRejected;

    public ClientStore Store => _store;

    /// <summary>
    /// Called inside the transaction that stores each new incoming message (of any type), so whatever it
    /// records or sends (events, receipts) commits with the message or not at all. Without one, a message is
    /// just recorded as <c>stored</c>.
    /// </summary>
    public IIncomingHandler? IncomingHandler { get; set; }

    /// <summary>The router's clock minus this machine's, as of the last Hello (null before the first).</summary>
    public TimeSpan? ClockOffset { get; private set; }

    /// <summary>How far the router lets clocks differ (from the last Hello).</summary>
    public TimeSpan? RouterClockSkew { get; private set; }

    /// <summary>
    /// Connected and authenticated as an approved machine on this connection. False from the moment a link
    /// drops until the reconnected link has proved its key again: the router refuses anything sent before that.
    /// </summary>
    public bool IsConnected => _authenticated && _connection.State == HubConnectionState.Connected;

    /// <summary>Connected to the router (whether or not this machine is approved yet).</summary>
    public bool IsReachable => _connection.State == HubConnectionState.Connected;

    public Uri RouterUrl { get; }

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

    /// <summary>Signs and sends a message to <c>machine</c> or <c>machine/agent</c> (machine by id or name).</summary>
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

        var targets = new List<(MachineInfo Machine, string Agent)>();
        foreach (var address in to.Select(Address.Parse).ToList()) // all parsed, resolved and key-checked before anything is sent
        {
            var machine = await ResolveAsync(address.Machine, cancellationToken);
            VerifiedEncryptionKey(machine);
            targets.Add((machine, address.Agent));
        }

        var envelopes = _store.Transact(_time.GetUtcNow(), tx => targets.Select(t => Compose(tx, t.Machine, t.Agent, message)).ToList());
        return await RelayComposedAsync(envelopes, cancellationToken);
    }

    /// <summary>Replies to <paramref name="original"/>: to its sender's agent, in its conversation, from the agent it was sent to.</summary>
    public async Task<SentMessage> ReplyAsync(ReceivedMessage original, OutgoingMessage reply, CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(original.Envelope.FromMachine, cancellationToken);
        var envelope = _store.Transact(_time.GetUtcNow(), tx => Compose(tx, target, original.Content.FromAgent, ReplyTo(original.Envelope.Id,
            original.Content, reply)));
        return (await RelayComposedAsync([envelope], cancellationToken))[0];
    }

    /// <summary>The owner's groups (named lists of addresses), as the router serves them.</summary>
    /// <summary>Signs a room event with this machine's key.</summary>
    public RoomEvent SignRoomEvent(RoomEvent e) => Signatures.SignRoomEvent(_keys, e);

    public Task<RoomState> CreateRoomAsync(RoomEvent create, CancellationToken cancellationToken = default) =>
        InvokeRoomAsync<RoomState>(HubMethods.CreateRoom, create, cancellationToken);

    public Task<RoomState> SubmitRoomEventAsync(RoomEvent e, CancellationToken cancellationToken = default) =>
        InvokeRoomAsync<RoomState>(HubMethods.SubmitRoomEvent, e, cancellationToken);

    public Task<RoomState> GetRoomAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        InvokeRoomAsync<RoomState>(HubMethods.GetRoom, roomId, cancellationToken);

    public Task<IReadOnlyList<RoomState>> GetMyRoomsAsync(IReadOnlyList<Guid> known, CancellationToken cancellationToken = default) =>
        InvokeRoomAsync<IReadOnlyList<RoomState>>(HubMethods.GetMyRooms, known, cancellationToken);

    private async Task<T> InvokeRoomAsync<T>(string method, object argument, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("not connected to the router: rooms need it");
        }

        try
        {
            return await _connection.InvokeAsync<T>(method, argument, cancellationToken);
        }
        catch (HubException e)
        {
            throw new InvalidOperationException(ReasonOf(e), e);
        }
    }

    public async Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        await _connection.InvokeAsync<IReadOnlyList<GroupInfo>>(HubMethods.GetGroups, cancellationToken);

    // ---- files ----

    /// <summary>
    /// Encrypts and uploads a file once, shared with <paramref name="shareWith"/> (machine ids; fixed from here
    /// on), and returns the attachment to put in each recipient's message. A chunk that fails to go up is
    /// retried with a fresh ticket; the encryption is deterministic for a key, so a retry sends the same bytes.
    /// </summary>
    public async Task<Attachment> UploadAsync(string path, IReadOnlyCollection<string> shareWith, string? contentType = null, string? name = null,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"no file {path}", path);
        }

        name ??= info.Name;
        contentType ??= "application/octet-stream";
        if (!BlobCrypto.IsValidFileName(name))
        {
            throw new ArgumentException($"'{name}' isn't a plain file name");
        }

        var size = info.Length;
        var chunks = BlobCrypto.ChunkCountOf(size);
        var key = BlobCrypto.NewKey();
        var ticket = await InvokeBlobAsync<BlobTicket>(HubMethods.BeginUpload,
            new BeginUploadRequest([.. shareWith], BlobCrypto.EncryptedSizeOf(size), chunks), cancellationToken);
        var id = ticket.BlobId;
        using var http = BlobHttp();
        var hashes = new List<string>(chunks);
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        {
            var buffer = new byte[BlobCrypto.ChunkSize];
            for (var i = 0; i < chunks; i++)
            {
                var read = await file.ReadAtLeastAsync(buffer, BlobCrypto.ChunkSize, throwOnEndOfStream: false, cancellationToken);
                var sealedChunk = BlobCrypto.SealChunk(key, id, i, i == chunks - 1, buffer.AsSpan(0, read));
                hashes.Add(BlobCrypto.HashOf(sealedChunk));
                ticket = await PutWithRetryAsync(http, ticket, $"chunks/{i}", sealedChunk, cancellationToken);
            }
        }

        var manifest = BlobCrypto.SealManifest(key, id, new BlobManifest(name, contentType, size, chunks, hashes));
        await PutWithRetryAsync(http, ticket, "manifest", manifest, cancellationToken);
        await InvokeBlobAsync<BlobStatus>(HubMethods.CompleteUpload, id, cancellationToken);
        return new Attachment(id, name, contentType, size, chunks, Convert.ToBase64String(key), BlobCrypto.ChunksHashOf(hashes));
    }

    /// <summary>
    /// Fetches an attachment to <paramref name="destination"/>, verifying everything against the message: the
    /// manifest's chunk list against its hash, each chunk against the list, each chunk's place (index, last).
    /// Written to a <c>.part</c> file first, so a file at <paramref name="destination"/> is always complete.
    /// </summary>
    /// <exception cref="BlobTransferException">With why; <see cref="BlobTransferException.Definitive"/> unless retrying may help.</exception>
    public async Task DownloadAsync(Attachment attachment, string destination, CancellationToken cancellationToken = default)
    {
        var key = Convert.FromBase64String(attachment.Key);
        var id = attachment.BlobId;
        BlobTicket ticket;
        try
        {
            ticket = await InvokeBlobAsync<BlobTicket>(HubMethods.RequestDownload, id, cancellationToken);
        }
        catch (HubException e)
        {
            throw new BlobTransferException(ReasonOf(e), definitive: true);
        }

        using var http = BlobHttp();
        BlobManifest manifest;
        try
        {
            manifest = BlobCrypto.OpenManifest(key, id, await GetAsync(http, ticket, "manifest", cancellationToken));
        }
        catch (CryptographicException)
        {
            throw new BlobTransferException("the manifest doesn't verify (tampered with, or not this message's file)", definitive: true);
        }

        if (manifest.ChunkHashes.Count != attachment.ChunkCount || manifest.ChunkCount != attachment.ChunkCount || manifest.Size != attachment.Size
            || BlobCrypto.ChunksHashOf(manifest.ChunkHashes) != attachment.ChunksHash)
        {
            throw new BlobTransferException("the manifest doesn't match the message (hash or size mismatch)", definitive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var part = destination + ".part";
        try
        {
            long written = 0;
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                for (var i = 0; i < manifest.ChunkCount; i++)
                {
                    var sealedChunk = await GetAsync(http, ticket, $"chunks/{i}", cancellationToken);
                    if (BlobCrypto.HashOf(sealedChunk) != manifest.ChunkHashes[i])
                    {
                        throw new BlobTransferException($"chunk {i} doesn't match its hash (tampered with)", definitive: true);
                    }

                    byte[] plain;
                    try
                    {
                        plain = BlobCrypto.OpenChunk(key, id, i, i == manifest.ChunkCount - 1, sealedChunk);
                    }
                    catch (CryptographicException)
                    {
                        throw new BlobTransferException($"chunk {i} doesn't decrypt (tampered with, or out of place)", definitive: true);
                    }

                    await file.WriteAsync(plain, cancellationToken);
                    written += plain.Length;
                }
            }

            if (written != attachment.Size)
            {
                throw new BlobTransferException($"the file is {written} bytes, not the {attachment.Size} the message says", definitive: true);
            }

            File.Move(part, destination, overwrite: true);
        }
        catch
        {
            File.Delete(part);
            throw;
        }
    }

    /// <summary>Tells the router this machine is done with a blob (it may delete it once every machine has).</summary>
    public Task ReleaseBlobAsync(Guid blobId, CancellationToken cancellationToken = default) =>
        _connection.InvokeAsync(HubMethods.ReleaseBlob, blobId, cancellationToken);

    public Task CancelBlobAsync(Guid blobId, CancellationToken cancellationToken = default) =>
        InvokeBlobAsync<object?>(HubMethods.CancelBlob, blobId, cancellationToken, noResult: true);

    public Task<IReadOnlyList<SenderBlob>> GetBlobsAsync(CancellationToken cancellationToken = default) =>
        InvokeBlobAsync<IReadOnlyList<SenderBlob>>(HubMethods.GetBlobs, null, cancellationToken);

    /// <summary>A refusal's own words, without SignalR's "An unexpected error occurred invoking ..." wrapper.</summary>
    public static string ReasonOf(Exception e)
    {
        const string Marker = "HubException: ";
        var at = e.Message.IndexOf(Marker, StringComparison.Ordinal);
        return at < 0 ? e.Message : e.Message[(at + Marker.Length)..];
    }

    private async Task<T> InvokeBlobAsync<T>(string method, object? argument, CancellationToken cancellationToken, bool noResult = false)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("not connected to the router: files need it (messages wait in the outbox, files don't)");
        }

        if (noResult)
        {
            await _connection.InvokeAsync(method, argument, cancellationToken);
            return default!;
        }

        return argument is null
            ? await _connection.InvokeAsync<T>(method, cancellationToken)
            : await _connection.InvokeAsync<T>(method, argument, cancellationToken);
    }

    private async Task<BlobTicket> PutWithRetryAsync(HttpClient http, BlobTicket ticket, string part, byte[] data, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, $"{HubMethods.BlobsPath}/{ticket.BlobId}/{part}") { Content = new ByteArrayContent(data) };
                request.Headers.TryAddWithoutValidation("Authorization", "Ticket " + ticket.Ticket);
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ticket;
                }

                var detail = await ProblemOf(response, cancellationToken);
                if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || attempt >= 3)
                {
                    throw new BlobTransferException($"the router refused {part}: {detail}", definitive: response.StatusCode != System.Net.HttpStatusCode.Unauthorized);
                }
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), _time, cancellationToken);
            }

            ticket = await InvokeBlobAsync<BlobTicket>(HubMethods.ResumeUpload, ticket.BlobId, cancellationToken);
        }
    }

    private static async Task<byte[]> GetAsync(HttpClient http, BlobTicket ticket, string part, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{HubMethods.BlobsPath}/{ticket.BlobId}/{part}");
        request.Headers.TryAddWithoutValidation("Authorization", "Ticket " + ticket.Ticket);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new BlobTransferException($"can't reach the router: {e.Message}", definitive: false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new BlobTransferException($"the router has no {part} ({(int)response.StatusCode}{await ProblemOf(response, cancellationToken)})",
                    definitive: response.StatusCode == System.Net.HttpStatusCode.NotFound);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > BlobCrypto.MaxSealedChunk)
            {
                throw new BlobTransferException($"{part} is larger than a chunk can be", definitive: true);
            }

            return bytes;
        }
    }

    private static async Task<string> ProblemOf(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
            return problem.TryGetProperty("detail", out var d) ? ": " + d.GetString() : "";
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>An HTTP client for the router's <c>/blobs</c> calls, with the same certificate pin as the hub connection.</summary>
    private HttpClient BlobHttp()
    {
        HttpMessageHandler handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = _pin is { } pin ? (_, cert, _, _) => cert is not null && Matches(cert, pin) : null,
        };
        if (_handlerFactory is not null)
        {
            handler = _handlerFactory(handler);
        }

        return new HttpClient(handler) { BaseAddress = RouterUrl, Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>The reply's fields filled in from the message it answers: its conversation, from the agent it was sent to.</summary>
    public static OutgoingMessage ReplyTo(Guid originalId, MessageContent original, OutgoingMessage reply) => reply with
    {
        FromAgent = reply.FromAgent ?? original.ToAgent,
        ConversationId = original.ConversationId,
        ReplyTo = originalId,
    };

    /// <summary>
    /// Builds, seals and stores a message for <paramref name="target"/> inside <paramref name="tx"/>, as
    /// <c>queued</c> in the outbox. Nothing leaves until <see cref="FlushOutboxAsync"/> (which
    /// <see cref="SendAsync(string, OutgoingMessage, CancellationToken)"/> calls).
    /// </summary>
    public Envelope Compose(StoreTransaction tx, MachineInfo target, string toAgent, OutgoingMessage message)
    {
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

        var now = tx.Now;
        var id = Guid.NewGuid();
        var content = new MessageContent(
            message.FromAgent ?? Address.DefaultAgent,
            toAgent,
            message.ConversationId ?? Guid.NewGuid(),
            message.ReplyTo,
            message.OperationId ?? id,
            message.Status,
            string.IsNullOrEmpty(message.Subject) ? null : message.Subject,
            message.Type,
            message.ContentType,
            message.Metadata ?? new Dictionary<string, string>(),
            message.Body,
            now,
            message.Attachments is { Count: > 0 } a ? a : null);
        if (MessageRules.CheckContent(content) is { } problem)
        {
            throw new ArgumentException(problem, nameof(message));
        }

        return tx.ReserveOutgoing(target.Id, content,
            sequence => Signatures.SealEnvelope(_keys, id, target.Id, recipientKey, sequence, now + lifetime, content));
    }

    /// <summary>
    /// Builds, seals and stores one room message inside <paramref name="tx"/>, as <c>queued</c>: its content key wrapped
    /// for every machine in <paramref name="machines"/> (each key checked as for any message) and this one. The router fans
    /// it out; nothing leaves until <see cref="FlushOutboxAsync"/>.
    /// </summary>
    public Envelope ComposeRoom(StoreTransaction tx, Guid roomId, IReadOnlyCollection<MachineInfo> machines, OutgoingMessage message)
    {
        if (message.Attachments is { Count: > 0 })
        {
            throw new ArgumentException("room messages don't carry files yet; send them to the members directly");
        }

        var readers = machines.Where(m => m.Id != _keys.MachineId).Select(m => new MessageCrypto.Reader(m.Id, VerifiedEncryptionKey(m))).ToList();
        var settings = _messaging.CurrentValue;
        var lifetime = message.ExpiresIn is { } e && e > TimeSpan.Zero && e < settings.MaxExpiry ? e : settings.DefaultExpiry;
        var now = tx.Now;
        var id = Guid.NewGuid();
        var content = new MessageContent(message.FromAgent ?? Address.DefaultAgent, RoomAddress.Agent, roomId, message.ReplyTo, message.OperationId ?? id,
            message.Status, string.IsNullOrEmpty(message.Subject) ? null : message.Subject, MessageType.Message, message.ContentType,
            message.Metadata ?? new Dictionary<string, string>(), message.Body, now);
        if (MessageRules.CheckContent(content) is { } problem)
        {
            throw new ArgumentException(problem, nameof(message));
        }

        return tx.ReserveOutgoing(RoomAddress.Of(roomId), content,
            sequence => Signatures.SealRoomEnvelope(_keys, id, roomId, readers, sequence, now + lifetime, content));
    }

    /// <summary>
    /// Relays everything in the outbox, oldest first. A message whose expiry has passed is marked
    /// <c>expired</c> and never relayed: the recipient would only refuse it. One the router refuses is
    /// <c>failed</c>. Without a connection, messages stay queued for the next flush (after a reconnect).
    /// </summary>
    public async Task FlushOutboxAsync(CancellationToken cancellationToken = default)
    {
        await _flushing.WaitAsync(cancellationToken);
        try
        {
            foreach (var message in _store.GetOutbox(_keys.MachineId))
            {
                if (_time.GetUtcNow() > message.ExpiresOn)
                {
                    _store.Transact(_time.GetUtcNow(), tx => tx.AddEvent(message.FromMachine, message.Id, MessageDirection.Out, message.FromAgent,
                        MessageEvents.Expired, "expired before it could be relayed"));
                    continue;
                }

                if (!IsConnected || message.Envelope is null)
                {
                    continue; // stays queued; expired ones further on are still marked
                }

                string @event;
                string detail;
                try
                {
                    var result = await _connection.InvokeAsync<SendResult>(HubMethods.Send, message.Envelope, cancellationToken);
                    (@event, detail) = (MessageEvents.Relayed, WireProtocol.NameOf(result.State));
                }
                catch (HubException e)
                {
                    (@event, detail) = (MessageEvents.Failed, e.Message);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    BackgroundError?.Invoke(e); // the link went; it stays queued
                    continue;
                }

                _store.Transact(_time.GetUtcNow(), tx => tx.AddEvent(message.FromMachine, message.Id, MessageDirection.Out, message.FromAgent,
                    @event, detail));
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    /// <summary>Opens (decrypts) an envelope this machine sent or received.</summary>
    public MessageContent Open(Envelope envelope) => MessageCrypto.Open(envelope, _keys.MachineId, _keys.Encryption);

    /// <summary>An approved machine, by id, from the last list the router gave (null if unknown).</summary>
    public MachineInfo? Machine(string id) => _machines.GetValueOrDefault(id);

    /// <summary>An approved machine by id or name, asking the router again if it isn't known yet.</summary>
    public async Task<MachineInfo> ResolveAsync(string to, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Flushes the outbox and reports on <paramref name="envelopes"/> (composed with <see cref="Compose"/>):
    /// relayed (with the router's state), or still in the outbox. Throws if the router refused one.
    /// </summary>
    public async Task<IReadOnlyList<SentMessage>> RelayComposedAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellationToken = default)
    {
        await FlushOutboxAsync(cancellationToken);
        var sent = new List<SentMessage>();
        foreach (var envelope in envelopes)
        {
            var last = _store.GetEvents(envelope.FromMachine, envelope.Id).Last(e => e.Direction == MessageDirection.Out);
            if (last.Event == MessageEvents.Failed)
            {
                throw new HubException(last.Detail);
            }

            var content = Open(envelope);
            DeliveryState? state = last.Event == MessageEvents.Relayed
                ? Enum.GetValues<DeliveryState>().First(s => WireProtocol.NameOf(s) == last.Detail)
                : null;
            sent.Add(new SentMessage(envelope.Id, envelope.ToMachine, content.ToAgent, content.ConversationId, envelope.Sequence, envelope.ExpiresOn, state));
        }

        return sent;
    }

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

    private async Task<MembershipStatus> AuthenticateAsync(CancellationToken cancellationToken)
    {
        _authenticated = false;
        var hello = await _connection.InvokeAsync<HelloResult>(HubMethods.Hello, WireProtocol.Version, cancellationToken);
        ClockOffset = hello.ServerTime - _time.GetUtcNow();
        RouterClockSkew = hello.ClockSkew;
        var challenge = await _connection.InvokeAsync<string>(HubMethods.GetChallenge, cancellationToken);
        var signature = _keys.Sign(Signatures.AuthPayload(challenge, _keys.PublicKey));
        var result = await _connection.InvokeAsync<AuthResult>(HubMethods.Authenticate, _keys.PublicKey, signature, cancellationToken);
        Status = result.Status;
        Name = result.Name;
        if (result.Status == MembershipStatus.Approved)
        {
            _authenticated = true;
            await RefreshMachinesAsync(cancellationToken);
            ConnectionChanged?.Invoke(true, null);
            Post(() => FlushOutboxAsync()); // whatever waited for the connection
            Post(RetryConfirmsAsync); // confirms that failed (e.g. sent just as the link dropped)
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
        _authenticated &= status == MembershipStatus.Approved;
        MembershipChanged?.Invoke(status);
        if (status == MembershipStatus.Approved)
        {
            await AuthenticateAsync(CancellationToken.None); // join the members' groups on this connection
        }
    }

    private async Task OnReceiveAsync(Delivery delivery)
    {
        var envelope = delivery.Envelope;
        if (envelope?.Ciphertext is null || (envelope.ToMachine != _keys.MachineId
                && !(RoomAddress.TryParse(envelope.ToMachine, out _) && envelope.Wraps?.Any(w => w.Machine == _keys.MachineId) == true)))
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
        var received = new ReceivedMessage(envelope, content, sender.Name, delivery.RelayedOn);
        if (refusal is { } r)
        {
            await _store.CountAsync(envelope.FromMachine, r.Metric, now);
            MessageRejected?.Invoke(envelope, r.Reason);
        }
        else
        {
            stored = _store.Transact(now, tx =>
            {
                if (!tx.InsertIncoming(envelope, content, delivery.RelayedOn, expiresOn, _keys.MachineId))
                {
                    return false;
                }

                if (IncomingHandler is { } incoming)
                {
                    incoming.OnStored(tx, received);
                }
                else
                {
                    tx.AddEvent(envelope.FromMachine, envelope.Id, MessageDirection.In, content.ToAgent, MessageEvents.Stored);
                }

                return true;
            });
        }

        // Stored (or refused for good): the router can drop it. A duplicate is confirmed again and dropped quietly.
        await ConfirmAsync(envelope.Id);

        if (stored && IncomingHandler is not null)
        {
            await FlushOutboxAsync(); // receipts it composed
        }

        if (stored && MessageReceived is { } handler)
        {
            await handler(received);
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
            _unconfirmed.TryRemove(id, out _);
        }
        catch (Exception e)
        {
            // Retried after the next authentication; if the router redelivers it first, it's a duplicate.
            _unconfirmed[id] = 0;
            BackgroundError?.Invoke(e);
        }
    }

    private async Task RetryConfirmsAsync()
    {
        foreach (var id in _unconfirmed.Keys)
        {
            await ConfirmAsync(id);
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
    /// <summary>The sender as <c>name/agent</c>.</summary>
    public Address From => new(FromName, Content.FromAgent);
}

/// <summary>
/// What to send. Unset ids are generated: a new conversation, and the operation id defaults to the message
/// id. <see cref="ExpiresIn"/> defaults to the configured default expiry and is cut to the maximum.
/// </summary>
public sealed record OutgoingMessage(string Body)
{
    public string? FromAgent { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? ReplyTo { get; init; }

    public Guid? OperationId { get; init; }

    /// <summary>For replies: the outcome of the work. Null is the success path.</summary>
    public ReplyStatus? Status { get; init; }

    public string? Subject { get; init; }

    public string ContentType { get; init; } = "text/plain";

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public TimeSpan? ExpiresIn { get; init; }

    /// <summary>Files already uploaded (see <see cref="RelayClient.UploadAsync"/>), shared with this message's recipient machine.</summary>
    public IReadOnlyList<Attachment>? Attachments { get; init; }

    /// <summary>Protocol level; only the daemon sends anything but <see cref="MessageType.Message"/>.</summary>
    public MessageType Type { get; init; } = MessageType.Message;
}

/// <summary>Hooks the storing of incoming messages (see <see cref="RelayClient.IncomingHandler"/>).</summary>
public interface IIncomingHandler
{
    /// <summary>Called inside the transaction that stored <paramref name="message"/>; record its first event here.</summary>
    void OnStored(StoreTransaction tx, ReceivedMessage message);
}

/// <summary>A message sent. <see cref="State"/> is the router's; null when it is still in the outbox (no connection).</summary>
public sealed record SentMessage(Guid Id, string ToMachine, string ToAgent, Guid ConversationId, long Sequence, DateTimeOffset ExpiresOn, DeliveryState? State);

/// <summary>A file transfer that failed. <see cref="Definitive"/>: retrying won't help (tampered, cancelled, expired, over a limit).</summary>
public sealed class BlobTransferException(string reason, bool definitive) : Exception(reason)
{
    public bool Definitive { get; } = definitive;
}
