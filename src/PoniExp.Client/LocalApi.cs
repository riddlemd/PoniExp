using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoniExp.Core.Protocol;

namespace PoniExp.Client;

/// <summary>
/// Where a daemon's local API listens: a named pipe on Windows (only its own user can connect), a Unix
/// socket elsewhere (owner-only, in the home directory or, if that path is too long, the temp directory).
/// Derived from the home directory, so each home has its own daemon.
/// </summary>
public sealed record LocalAddress(string? PipeName, string? SocketPath)
{
    /// <summary>The system-mode daemon's home (a boot-time service).</summary>
    public static string SystemHome =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PoniExp")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/PoniExp"
        : "/var/lib/poniexp";

    public static LocalAddress ForHome(string home)
    {
        var full = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full)))[..16];
        if (OperatingSystem.IsWindows())
        {
            return new LocalAddress($"poniexp-{hash}", null);
        }

        var socket = Path.Combine(full, "daemon.sock");
        return new LocalAddress(null, socket.Length < 100 ? socket : Path.Combine(Path.GetTempPath(), $"poniexp-{hash}.sock"));
    }

    public override string ToString() => PipeName is not null ? $@"\\.\pipe\{PipeName}" : SocketPath!;
}

/// <summary>A refusal from the local API (bad request, unknown endpoint, ...), with the daemon's reason.</summary>
public sealed class LocalApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The daemon's local API, for the CLI, the MCP servers and other local agents. Each call names the
/// endpoint it acts for, and is shown only that endpoint's messages (scoping, not a security boundary:
/// every local process of the machine's user is trusted).
/// </summary>
public sealed class LocalApiClient : IDisposable
{
    private readonly HttpClient _http;

    public LocalApiClient(LocalAddress address)
    {
        Address = address;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                if (address.PipeName is { } pipe)
                {
                    // ConnectAsync waits for a pipe that doesn't exist yet; a daemon that isn't running should fail at once.
                    if (!File.Exists($@"\\.\pipe\{pipe}"))
                    {
                        throw new IOException($"no daemon is listening on {address}");
                    }

                    var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await stream.ConnectAsync((int)TimeSpan.FromSeconds(10).TotalMilliseconds, cancellationToken);
                        return stream;
                    }
                    catch
                    {
                        await stream.DisposeAsync();
                        throw;
                    }
                }

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(address.SocketPath!), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://poniexp.local/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public LocalAddress Address { get; }

    /// <summary>The API for <paramref name="home"/>'s daemon; failing that, a running system-mode daemon's.</summary>
    public static async Task<LocalApiClient> FindAsync(string home, CancellationToken cancellationToken = default)
    {
        var mine = new LocalApiClient(LocalAddress.ForHome(home));
        if (await mine.IsRunningAsync(cancellationToken) || Path.GetFullPath(home) == Path.GetFullPath(LocalAddress.SystemHome))
        {
            return mine;
        }

        var system = new LocalApiClient(LocalAddress.ForHome(LocalAddress.SystemHome));
        if (await system.IsRunningAsync(cancellationToken))
        {
            mine.Dispose();
            return system;
        }

        system.Dispose();
        return mine;
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await _http.GetAsync("health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or TimeoutException)
        {
            return false;
        }
    }

    public Task<LocalStatus> StatusAsync(CancellationToken ct = default) => GetAsync<LocalStatus>("status", ct);

    public Task<List<LocalMachine>> MachinesAsync(CancellationToken ct = default) => GetAsync<List<LocalMachine>>("machines", ct);

    public Task<List<LocalSent>> SendAsync(LocalSendRequest request, CancellationToken ct = default) => PostAsync<List<LocalSent>>("send", request, ct);

    public Task<LocalSent> ReplyAsync(string endpoint, LocalReplyRequest request, CancellationToken ct = default) =>
        PostAsync<LocalSent>($"endpoints/{endpoint}/reply", request, ct);

    public Task<LocalMessage> AckAsync(string endpoint, Guid id, CancellationToken ct = default) =>
        PostAsync<LocalMessage>($"endpoints/{endpoint}/ack", new LocalAckRequest(id), ct);

    /// <summary>Messages for the endpoint that haven't been acknowledged; fetching them doesn't acknowledge them.</summary>
    public Task<List<LocalMessage>> InboxAsync(string endpoint, CancellationToken ct = default) => GetAsync<List<LocalMessage>>($"endpoints/{endpoint}/inbox", ct);

    public Task<LocalMessageDetail> MessageAsync(string endpoint, Guid id, CancellationToken ct = default) =>
        GetAsync<LocalMessageDetail>($"endpoints/{endpoint}/messages/{id}", ct);

    public Task<List<LocalMessage>> ConversationAsync(string endpoint, Guid conversationId, CancellationToken ct = default) =>
        GetAsync<List<LocalMessage>>($"endpoints/{endpoint}/conversations/{conversationId}", ct);

    /// <summary>The endpoint's conversations, most recent first: what an agent picks up again after a restart.</summary>
    public Task<List<LocalConversation>> ConversationsAsync(string endpoint, CancellationToken ct = default) =>
        GetAsync<List<LocalConversation>>($"endpoints/{endpoint}/conversations", ct);

    /// <summary>The machine's message log (see <see cref="LocalLogQuery"/>), oldest first.</summary>
    public Task<List<LocalLogEntry>> LogAsync(LocalLogQuery q, CancellationToken ct = default)
    {
        var query = new List<string>();
        void Add(string name, object? value)
        {
            if (value is not null)
            {
                query.Add($"{name}={Uri.EscapeDataString(value is DateTimeOffset d ? d.ToString("O") : value.ToString()!)}");
            }
        }

        Add("conversation", q.Conversation);
        Add("operation", q.Operation);
        Add("peer", q.Peer);
        Add("endpoint", q.Endpoint);
        Add("since", q.Since);
        Add("limit", q.Limit);
        Add("automatic", q.Automatic ? "true" : null);
        Add("bodies", q.Bodies ? "true" : null);
        return GetAsync<List<LocalLogEntry>>("log" + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query)), ct);
    }

    /// <summary>Fetches a received message's files to this machine (verified); returns where they are.</summary>
    public Task<List<LocalAttachment>> FetchAsync(string endpoint, LocalFileRequest request, CancellationToken ct = default) =>
        PostAsync<List<LocalAttachment>>($"endpoints/{endpoint}/fetch", request, ct);

    /// <summary>The endpoint is done with the files; the local copy goes once every endpoint that received it is.</summary>
    public Task<List<LocalAttachment>> ReleaseAsync(string endpoint, LocalFileRequest request, CancellationToken ct = default) =>
        PostAsync<List<LocalAttachment>>($"endpoints/{endpoint}/release", request, ct);

    /// <summary>The files this machine has uploaded, as the router sees them.</summary>
    public Task<List<SenderBlob>> BlobsAsync(CancellationToken ct = default) => GetAsync<List<SenderBlob>>("blobs", ct);

    public async Task CancelBlobAsync(Guid blobId, CancellationToken ct = default) => await PostAsync<LocalOk>($"blobs/{blobId}/cancel", new { }, ct);

    /// <summary>Rooms with a member here (or just <paramref name="endpoint"/>'s).</summary>
    public Task<List<LocalRoom>> RoomsAsync(string? endpoint = null, CancellationToken ct = default) =>
        GetAsync<List<LocalRoom>>("rooms" + (endpoint is null ? "" : "?endpoint=" + Uri.EscapeDataString(endpoint)), ct);

    public Task<LocalRoom> CreateRoomAsync(LocalRoomCreate request, CancellationToken ct = default) => PostAsync<LocalRoom>("rooms", request, ct);

    public Task<LocalRoom> ChangeRoomAsync(string room, LocalRoomChange request, CancellationToken ct = default) =>
        PostAsync<LocalRoom>($"rooms/{Uri.EscapeDataString(room)}/change", request, ct);

    public Task<LocalSent> SendToRoomAsync(string room, LocalRoomSend request, CancellationToken ct = default) =>
        PostAsync<LocalSent>($"rooms/{Uri.EscapeDataString(room)}/send", request, ct);

    /// <summary>A room's messages, oldest first: the most recent <paramref name="limit"/>, after <paramref name="since"/> if given.</summary>
    public Task<List<LocalMessage>> ReadRoomAsync(string room, string endpoint, DateTimeOffset? since = null, int? limit = null, CancellationToken ct = default) =>
        GetAsync<List<LocalMessage>>($"rooms/{Uri.EscapeDataString(room)}/messages?endpoint={Uri.EscapeDataString(endpoint)}" +
                                     (since is { } s ? "&since=" + Uri.EscapeDataString(s.ToString("O")) : "") + (limit is { } l ? $"&limit={l}" : ""), ct);

    public Task<List<LocalEndpointInfo>> EndpointsAsync(CancellationToken ct = default) => GetAsync<List<LocalEndpointInfo>>("endpoints", ct);

    public Task<LocalEndpointInfo> RetireAsync(string endpoint, CancellationToken ct = default) =>
        PostAsync<LocalEndpointInfo>($"endpoints/{endpoint}/retire", new { }, ct);

    public Task<List<Diagnostic>> DiagnosticsAsync(CancellationToken ct = default) => GetAsync<List<Diagnostic>>("diagnostics", ct);

    /// <summary>Asks the router to admit this machine, through the daemon (which holds the keys).</summary>
    public Task<LocalJoinResult> JoinAsync(string name, string? notes, CancellationToken ct = default) =>
        PostAsync<LocalJoinResult>("join", new LocalJoinRequest(name, notes), ct);

    /// <summary>Stops the daemon.</summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        using var timeout = WithTimeout(ct);
        using var response = await _http.PostAsync("shutdown", null, timeout.Token);
        await EnsureAsync(response, timeout.Token);
    }

    public async Task HeartbeatAsync(string endpoint, CancellationToken ct = default) =>
        await PostAsync<JsonElement>($"endpoints/{endpoint}/heartbeat", new { }, ct);

    /// <summary>Unregisters the endpoint cleanly (it shows as left, not timed out).</summary>
    public async Task LeaveAsync(string endpoint, CancellationToken ct = default)
    {
        using var timeout = WithTimeout(ct);
        using var response = await _http.DeleteAsync($"endpoints/{endpoint}", timeout.Token);
        await EnsureAsync(response, timeout.Token);
    }

    /// <summary>
    /// Registers the endpoint (it is online while this runs) and yields what arrives for it: its messages,
    /// and receipts for what it sent. Messages not yet acknowledged are delivered again on each registration.
    /// Keep it alive with <see cref="HeartbeatAsync"/>, or use <see cref="EndpointConnection"/>.
    /// </summary>
    public async IAsyncEnumerable<LocalEvent> StreamAsync(string endpoint, [EnumeratorCancellation] CancellationToken ct = default,
        IReadOnlyList<string>? roles = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"endpoints/{endpoint}/stream{(roles is { Count: > 0 } ? "?roles=" + Uri.EscapeDataString(string.Join(',', roles)) : "")}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal)
                && JsonSerializer.Deserialize<LocalEvent>(line["data: ".Length..], WireProtocol.Json) is { } e)
            {
                yield return e;
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var timeout = WithTimeout(ct);
        using var response = await _http.GetAsync(path, timeout.Token);
        return await ReadAsync<T>(response, timeout.Token);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var timeout = WithTimeout(ct);
        using var response = await _http.PostAsJsonAsync(path, body, WireProtocol.Json, timeout.Token);
        return await ReadAsync<T>(response, timeout.Token);
    }

    private static CancellationTokenSource WithTimeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        return timeout;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(WireProtocol.Json, ct))!;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        string message;
        try
        {
            message = JsonSerializer.Deserialize<LocalError>(text, WireProtocol.Json)?.Error ?? text;
        }
        catch (JsonException)
        {
            message = text;
        }

        throw new LocalApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "error" : message);
    }
}

/// <summary>
/// Keeps an endpoint registered with its daemon: holds the stream, sends heartbeats, and reconnects if the
/// daemon restarts. Dispose (or cancel) to leave cleanly.
/// </summary>
public sealed class EndpointConnection : IAsyncDisposable
{
    private readonly LocalApiClient _api;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    /// <param name="roles">The roles this endpoint declares (announced to the other machines); null keeps what it declared before.</param>
    public EndpointConnection(LocalApiClient api, string endpoint, Func<LocalEvent, Task> onEvent, TimeSpan? heartbeat = null,
        Action<Exception>? onError = null, IReadOnlyList<string>? roles = null)
    {
        _api = api;
        Endpoint = endpoint;
        _roles = roles;
        _run = Task.Run(() => RunAsync(onEvent, heartbeat ?? TimeSpan.FromSeconds(15), onError));
    }

    public string Endpoint { get; }

    private readonly IReadOnlyList<string>? _roles;

    /// <summary>Completes once the endpoint is registered (each time after a reconnect it is set again).</summary>
    public Task Registered => _registered.Task;

    private TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _api.LeaveAsync(Endpoint);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or LocalApiException or OperationCanceledException)
        {
            // daemon gone; nothing to leave
        }

        await _stop.CancelAsync();
        try
        {
            await _run;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(Func<LocalEvent, Task> onEvent, TimeSpan heartbeat, Action<Exception>? onError)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var beating = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(heartbeat);
                while (await timer.WaitForNextTickAsync(session.Token))
                {
                    try
                    {
                        await _api.HeartbeatAsync(Endpoint, session.Token);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        onError?.Invoke(e);
                    }
                }
            });
            try
            {
                await foreach (var e in _api.StreamAsync(Endpoint, session.Token, _roles))
                {
                    if (e.Kind == LocalEventKinds.Registered)
                    {
                        _registered.TrySetResult();
                        continue;
                    }

                    await onEvent(e);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                onError?.Invoke(e);
            }
            finally
            {
                await session.CancelAsync();
                try
                {
                    await beating;
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (_registered.Task.IsCompleted)
            {
                _registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

public static class LocalEventKinds
{
    /// <summary>The first event on a stream: the endpoint is registered and online.</summary>
    public const string Registered = "registered";
    /// <summary>A message for the endpoint.</summary>
    public const string Message = "message";
    /// <summary>A delivery receipt for a message the endpoint sent.</summary>
    public const string Receipt = "receipt";
    /// <summary>A message in a room the endpoint is a member of.</summary>
    public const string RoomMessage = "room-message";
    /// <summary>A file receipt (downloaded, download-failed) for a file the endpoint sent.</summary>
    public const string FileReceipt = "file-receipt";
}

/// <summary>A message as an agent sees it, decrypted. <see cref="From"/> and <see cref="To"/> are <c>machine-name/endpoint</c>.</summary>
public sealed record LocalMessage(
    Guid Id,
    string FromMachine,
    string From,
    string To,
    string Direction,
    Guid ConversationId,
    Guid? ReplyTo,
    Guid OperationId,
    string? Status,
    string? Subject,
    string Type,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    string Body,
    DateTimeOffset CreatedOn,
    DateTimeOffset ExpiresOn,
    string? State,
    string? StateDetail,
    IReadOnlyList<LocalAttachment>? Attachments = null);

public sealed record LocalMessageDetail(LocalMessage Message, IReadOnlyList<MessageEvent> Events);

/// <summary>A delivery receipt for <see cref="MessageId"/>: received, acknowledged or rejected (with a reason).</summary>
public sealed record LocalReceipt(Guid MessageId, string Receipt, string? Reason, string From);

public sealed record LocalEvent(string Kind, LocalMessage? Message = null, LocalReceipt? Receipt = null, LocalFileReceipt? FileReceipt = null);

/// <summary>A file receipt for <see cref="BlobId"/>, sent with <see cref="MessageId"/>: downloaded, or download-failed (with why).</summary>
public sealed record LocalFileReceipt(Guid MessageId, Guid BlobId, string Receipt, string? Reason, string From);

/// <summary>
/// A file with a message. <see cref="State"/>, received: waiting, downloaded (<see cref="Path"/> is the local copy),
/// download-failed, released or deleted. Sent: uploaded, downloaded, download-failed or cancelled.
/// </summary>
public sealed record LocalAttachment(Guid BlobId, string Name, string ContentType, long Size, string State, string? Path, string? Detail);

/// <summary>Fetch (or release) a message's files: all of them, or the one blob.</summary>
public sealed record LocalFileRequest(Guid Id, Guid? BlobId = null);

public sealed record LocalSendRequest(
    string From,
    IReadOnlyList<string> To,
    string Body,
    string? Subject = null,
    Guid? ConversationId = null,
    Guid? ReplyTo = null,
    Guid? OperationId = null,
    string? Status = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    TimeSpan? ExpiresIn = null,
    IReadOnlyList<string>? Attachments = null);

public sealed record LocalReplyRequest(
    Guid Id,
    string Body,
    string? Status = null,
    string? Subject = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    TimeSpan? ExpiresIn = null,
    IReadOnlyList<string>? Attachments = null);

public sealed record LocalAckRequest(Guid Id);

/// <summary>A conversation as one local endpoint sees it; <see cref="Peer"/> is <c>machine-name/endpoint</c>, <see cref="State"/> open or finished.</summary>
public sealed record LocalConversation(Guid Id, string Peer, string? Subject, string StartedBy, DateTimeOffset StartedOn, DateTimeOffset LastActivityOn,
    string State);

/// <summary>Log filters; null means any. <see cref="Peer"/> is a machine name or id. Bodies are decrypted only with <see cref="Bodies"/>.</summary>
public sealed record LocalLogQuery(Guid? Conversation = null, Guid? Operation = null, string? Peer = null, string? Endpoint = null,
    DateTimeOffset? Since = null, int? Limit = null, bool Automatic = false, bool Bodies = false);

/// <summary>
/// One message in the log. <see cref="Body"/> is null unless bodies were asked for; <see cref="BodyDropped"/>
/// says retention dropped the envelope, so there's no body left to show.
/// </summary>
public sealed record LocalLogEntry(Guid Id, string From, string To, string Direction, string Type, Guid ConversationId, Guid? ReplyTo, Guid OperationId,
    string? Status, string? Subject, string ContentType, long Sequence, DateTimeOffset CreatedOn, DateTimeOffset StoredOn, DateTimeOffset ExpiresOn,
    string? State, string? StateDetail, bool BodyDropped, string? Body = null, IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>A message sent: to <c>machine-name/endpoint</c>; <see cref="State"/> is pushed, queued (by the router) or outbox (not relayed yet).</summary>
public sealed record LocalSent(Guid Id, string To, Guid ConversationId, long Sequence, DateTimeOffset ExpiresOn, string State);

public sealed record LocalEndpointInfo(string Name, string State, DateTimeOffset ChangedOn, DateTimeOffset? RetiredOn, IReadOnlyList<string>? Roles = null);

public sealed record LocalPeerEndpoint(string Name, string State, DateTimeOffset ChangedOn, IReadOnlyList<string>? Roles = null);

/// <summary>An approved machine and its announced endpoints (all offline when the machine is).</summary>
public sealed record LocalMachine(string Id, string Name, bool Online, DateTimeOffset? LastSeenOn, string OperatingSystem,
    IReadOnlyList<LocalPeerEndpoint> Endpoints);

public sealed record LocalStatus(
    string MachineId,
    string? Name,
    string Membership,
    bool Connected,
    bool Reachable,
    string RouterUrl,
    TimeSpan? ClockOffset,
    TimeSpan? RouterClockSkew,
    string Home,
    int Outbox,
    IReadOnlyList<LocalEndpointInfo> Endpoints,
    IReadOnlyList<ClientMetric> Metrics);

public sealed record LocalError(string Error);

public sealed record LocalJoinRequest(string Name, string? Notes);

/// <summary>Where the machine stands after asking to join; compare <see cref="JoinCode"/> on the router before approving.</summary>
public sealed record LocalJoinResult(string Membership, string? Name, string MachineId, string JoinCode);

public sealed record LocalOk(bool Ok);

/// <summary>A room as a member machine knows it. Members and admins are <c>machine-name/endpoint</c>.</summary>
public sealed record LocalRoom(Guid Id, string Name, long Version, IReadOnlyList<string> Members, IReadOnlyList<string> Admins, bool Closed);

public sealed record LocalRoomCreate(string Endpoint, string Name, IReadOnlyList<string>? Members = null);

/// <summary>A change to a room by one of its admins (or <see cref="RoomChange.Leave"/> by any member).</summary>
public sealed record LocalRoomChange(string Endpoint, RoomChange Change);

/// <summary>What to change. Addresses may be <c>machine/endpoint</c>, roles or groups (resolved when the change is made).</summary>
public sealed record RoomChange(IReadOnlyList<string>? Add = null, IReadOnlyList<string>? Remove = null, IReadOnlyList<string>? AddAdmins = null,
    IReadOnlyList<string>? RemoveAdmins = null, string? Name = null, bool? Close = null, bool Leave = false);

public sealed record LocalRoomSend(string Endpoint, string Body, Guid? ReplyTo = null, string? Subject = null, string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
