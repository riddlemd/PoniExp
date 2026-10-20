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

/// <summary>A refusal from the local API (bad request, unknown agent, ...), with the daemon's reason.</summary>
public sealed class LocalApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The daemon's local API, for the CLI, the MCP servers and other local agents. Each call names the
/// agent it acts for, and is shown only that agent's messages (scoping, not a security boundary:
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

    public Task<LocalSent> ReplyAsync(string agent, LocalReplyRequest request, CancellationToken ct = default) =>
        PostAsync<LocalSent>($"agents/{agent}/reply", request, ct);

    public Task<LocalMessage> AckAsync(string agent, Guid id, CancellationToken ct = default) =>
        PostAsync<LocalMessage>($"agents/{agent}/ack", new LocalAckRequest(id), ct);

    /// <summary>Messages for the agent that haven't been acknowledged; fetching them doesn't acknowledge them.</summary>
    public Task<List<LocalMessage>> InboxAsync(string agent, CancellationToken ct = default) => GetAsync<List<LocalMessage>>($"agents/{agent}/inbox", ct);

    public Task<LocalMessageDetail> MessageAsync(string agent, Guid id, CancellationToken ct = default) =>
        GetAsync<LocalMessageDetail>($"agents/{agent}/messages/{id}", ct);

    public Task<List<LocalMessage>> ConversationAsync(string agent, Guid conversationId, CancellationToken ct = default) =>
        GetAsync<List<LocalMessage>>($"agents/{agent}/conversations/{conversationId}", ct);

    /// <summary>The agent's conversations, most recent first: what an agent picks up again after a restart.</summary>
    public Task<List<LocalConversation>> ConversationsAsync(string agent, CancellationToken ct = default) =>
        GetAsync<List<LocalConversation>>($"agents/{agent}/conversations", ct);

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
        Add("agent", q.Agent);
        Add("since", q.Since);
        Add("limit", q.Limit);
        Add("automatic", q.Automatic ? "true" : null);
        Add("bodies", q.Bodies ? "true" : null);
        return GetAsync<List<LocalLogEntry>>("log" + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query)), ct);
    }

    /// <summary>Fetches a received message's files to this machine (verified); returns where they are.</summary>
    public Task<List<LocalAttachment>> FetchAsync(string agent, LocalFileRequest request, CancellationToken ct = default) =>
        PostAsync<List<LocalAttachment>>($"agents/{agent}/fetch", request, ct);

    /// <summary>The agent is done with the files; the local copy goes once every agent that received it is.</summary>
    public Task<List<LocalAttachment>> ReleaseAsync(string agent, LocalFileRequest request, CancellationToken ct = default) =>
        PostAsync<List<LocalAttachment>>($"agents/{agent}/release", request, ct);

    /// <summary>The files this machine has uploaded, as the router sees them.</summary>
    public Task<List<SenderBlob>> BlobsAsync(CancellationToken ct = default) => GetAsync<List<SenderBlob>>("blobs", ct);

    public async Task CancelBlobAsync(Guid blobId, CancellationToken ct = default) => await PostAsync<LocalOk>($"blobs/{blobId}/cancel", new { }, ct);

    /// <summary>The board: unfinished jobs (and finished ones with <paramref name="all"/>), optionally only one column.</summary>
    public Task<List<LocalJob>> JobsAsync(bool all = false, string? column = null, CancellationToken ct = default) =>
        GetAsync<List<LocalJob>>("jobs?all=" + (all ? "true" : "false") + (column is null ? "" : "&column=" + Uri.EscapeDataString(column)), ct);

    /// <summary>What this machine has (detected by the daemon), which job requirements are checked against.</summary>
    public Task<LocalCapabilities> CapabilitiesAsync(CancellationToken ct = default) => GetAsync<LocalCapabilities>("capabilities", ct);

    public Task<LocalJob> JobAsync(string id, CancellationToken ct = default) => GetAsync<LocalJob>($"jobs/{Uri.EscapeDataString(id)}", ct);

    public Task<LocalJob> PostJobAsync(LocalJobPost request, CancellationToken ct = default) => PostAsync<LocalJob>("jobs", request, ct);

    /// <summary><paramref name="verb"/>: claim, release, cancel, comment or move.</summary>
    public Task<LocalJob> JobActionAsync(string id, string verb, LocalJobAction request, CancellationToken ct = default) =>
        PostAsync<LocalJob>($"jobs/{Uri.EscapeDataString(id)}/{verb}", request, ct);

    // ---- the owner, the desk and approvals ----

    public Task<LocalOwner> OwnerAsync(CancellationToken ct = default) => GetAsync<LocalOwner>("owner", ct);

    /// <summary>Pins the owner key (the line the router has; the caller has shown its fingerprint and had a yes).</summary>
    public Task<LocalOwner> PinOwnerAsync(string key, CancellationToken ct = default) => PostAsync<LocalOwner>("owner/pin", new LocalOwnerPin(key), ct);

    /// <summary>Makes this machine the desk with an owner-signed claim (the caller had the SSH agent sign it).</summary>
    public Task<LocalOwner> HereAsync(HereClaim claim, CancellationToken ct = default) => PostAsync<LocalOwner>("here", claim, ct);

    /// <summary>Ends the desk: from the desk machine itself no signature is needed; elsewhere an owner-signed away claim.</summary>
    public Task<LocalOwner> AwayAsync(HereClaim? signed = null, CancellationToken ct = default) => PostAsync<LocalOwner>("away", new LocalAway(signed), ct);

    /// <summary>Asks the owner. Returns at once (pending, or approved by a rule); then <see cref="WaitApprovalAsync"/>.</summary>
    public Task<LocalApproval> RequestApprovalAsync(LocalApprovalRequest request, CancellationToken ct = default) =>
        PostAsync<LocalApproval>("approvals", request, ct);

    /// <summary>The approval, once answered or after <paramref name="wait"/> (at most 50 seconds), whichever is first.</summary>
    public Task<LocalApproval> WaitApprovalAsync(Guid id, TimeSpan wait, CancellationToken ct = default) =>
        GetAsync<LocalApproval>($"approvals/{id}?wait={(int)Math.Clamp(wait.TotalSeconds, 0, 50)}", ct);

    /// <summary>Asks and waits for the answer (withdrawn if <paramref name="ct"/> is cancelled first).</summary>
    public async Task<LocalApproval> AskAsync(LocalApprovalRequest request, CancellationToken ct = default)
    {
        request = request with { Id = request.Id ?? Guid.NewGuid() };
        try
        {
            var approval = await RequestApprovalAsync(request, ct);
            while (approval.State == ApprovalRecord.Pending)
            {
                approval = await WaitApprovalAsync(approval.Id, TimeSpan.FromSeconds(50), ct);
            }

            return approval;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await WithdrawApprovalAsync(request.Id.Value, CancellationToken.None);
            throw;
        }
    }

    public async Task WithdrawApprovalAsync(Guid id, CancellationToken ct = default)
    {
        using var timeout = WithTimeout(ct);
        using var response = await _http.DeleteAsync($"approvals/{id}", timeout.Token);
        await EnsureAsync(response, timeout.Token);
    }

    /// <summary>Approvals this machine's agents asked for, newest first.</summary>
    public Task<List<LocalApproval>> ApprovalsAsync(CancellationToken ct = default) => GetAsync<List<LocalApproval>>("approvals", ct);

    /// <summary>What waits on this desk (empty unless this machine is the desk).</summary>
    public Task<List<LocalApproval>> DeskAsync(CancellationToken ct = default) => GetAsync<List<LocalApproval>>("desk", ct);

    /// <summary>Approves (<paramref name="always"/>: and makes an "always allow" rule) or denies, by id or id prefix.</summary>
    public Task<LocalApproval> AnswerAsync(string id, bool approve, bool always = false, string? reason = null, CancellationToken ct = default) =>
        PostAsync<LocalApproval>($"desk/{Uri.EscapeDataString(id)}/{(approve ? "approve" : "deny")}", new LocalAnswer(always, reason), ct);

    /// <summary>Runs an owner-signed admin command on the router (the caller had the SSH agent sign it).</summary>
    public Task<AdminResult> AdminAsync(AdminCommand command, CancellationToken ct = default) => PostAsync<AdminResult>("admin", command, ct);

    public Task<List<ApprovalRule>> ApprovalRulesAsync(CancellationToken ct = default) => GetAsync<List<ApprovalRule>>("approvals/rules", ct);

    public async Task RemoveApprovalRuleAsync(string id, CancellationToken ct = default)
    {
        using var timeout = WithTimeout(ct);
        using var response = await _http.DeleteAsync($"approvals/rules/{Uri.EscapeDataString(id)}", timeout.Token);
        await EnsureAsync(response, timeout.Token);
    }

    /// <summary>Rooms with an agent of this machine in them or invited (or just <paramref name="agent"/>'s), collapsed ones last.</summary>
    public Task<List<LocalRoom>> RoomsAsync(string? agent = null, CancellationToken ct = default) =>
        GetAsync<List<LocalRoom>>("rooms" + (agent is null ? "" : "?agent=" + Uri.EscapeDataString(agent)), ct);

    /// <summary>Open invitations for <paramref name="agent"/> (or every local agent).</summary>
    public Task<List<LocalRoomInvite>> RoomInvitesAsync(string? agent = null, CancellationToken ct = default) =>
        GetAsync<List<LocalRoomInvite>>("rooms/invites" + (agent is null ? "" : "?agent=" + Uri.EscapeDataString(agent)), ct);

    public Task<LocalRoom> CreateRoomAsync(LocalRoomCreate request, CancellationToken ct = default) => PostAsync<LocalRoom>("rooms", request, ct);

    public Task<LocalRoom> InviteToRoomAsync(string room, LocalRoomInviteRequest request, CancellationToken ct = default) =>
        PostAsync<LocalRoom>($"rooms/{Uri.EscapeDataString(room)}/invite", request, ct);

    public Task<LocalRoom> JoinRoomAsync(string room, string agent, CancellationToken ct = default) =>
        PostAsync<LocalRoom>($"rooms/{Uri.EscapeDataString(room)}/join", new LocalRoomAgent(agent), ct);

    public Task<LocalRoom> LeaveRoomAsync(string room, string agent, CancellationToken ct = default) =>
        PostAsync<LocalRoom>($"rooms/{Uri.EscapeDataString(room)}/leave", new LocalRoomAgent(agent), ct);

    public Task<LocalSent> SendToRoomAsync(string room, LocalRoomSend request, CancellationToken ct = default) =>
        PostAsync<LocalSent>($"rooms/{Uri.EscapeDataString(room)}/send", request, ct);

    /// <summary>A room's messages, oldest first: the most recent <paramref name="limit"/>, after <paramref name="since"/> if given.</summary>
    public Task<List<LocalMessage>> ReadRoomAsync(string room, string agent, DateTimeOffset? since = null, int? limit = null, CancellationToken ct = default) =>
        GetAsync<List<LocalMessage>>($"rooms/{Uri.EscapeDataString(room)}/messages?agent={Uri.EscapeDataString(agent)}" +
                                     (since is { } s ? "&since=" + Uri.EscapeDataString(s.ToString("O")) : "") + (limit is { } l ? $"&limit={l}" : ""), ct);

    public Task<List<LocalAgentInfo>> AgentsAsync(CancellationToken ct = default) => GetAsync<List<LocalAgentInfo>>("agents", ct);

    public Task<LocalAgentInfo> RetireAsync(string agent, CancellationToken ct = default) =>
        PostAsync<LocalAgentInfo>($"agents/{agent}/retire", new { }, ct);

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

    public async Task HeartbeatAsync(string agent, CancellationToken ct = default) =>
        await PostAsync<JsonElement>($"agents/{agent}/heartbeat", new { }, ct);

    /// <summary>Unregisters the agent cleanly (it shows as left, not timed out).</summary>
    public async Task LeaveAsync(string agent, CancellationToken ct = default)
    {
        using var timeout = WithTimeout(ct);
        using var response = await _http.DeleteAsync($"agents/{agent}", timeout.Token);
        await EnsureAsync(response, timeout.Token);
    }

    /// <summary>
    /// Registers the agent (it is online while this runs) and yields what arrives for it: its messages,
    /// and receipts for what it sent. Messages not yet acknowledged are delivered again on each registration.
    /// Keep it alive with <see cref="HeartbeatAsync"/>, or use <see cref="AgentConnection"/>.
    /// </summary>
    public async IAsyncEnumerable<LocalEvent> StreamAsync(string agent, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"agents/{agent}/stream");
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
/// Keeps an agent registered with its daemon: holds the stream, sends heartbeats, and reconnects if the
/// daemon restarts. Dispose (or cancel) to leave cleanly.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    private readonly LocalApiClient _api;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    public AgentConnection(LocalApiClient api, string agent, Func<LocalEvent, Task> onEvent, TimeSpan? heartbeat = null,
        Action<Exception>? onError = null)
    {
        _api = api;
        Agent = agent;
        _run = Task.Run(() => RunAsync(onEvent, heartbeat ?? TimeSpan.FromSeconds(15), onError));
    }

    public string Agent { get; }

    /// <summary>Completes once the agent is registered (each time after a reconnect it is set again).</summary>
    public Task Registered => _registered.Task;

    private TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _api.LeaveAsync(Agent);
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
                        await _api.HeartbeatAsync(Agent, session.Token);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        onError?.Invoke(e);
                    }
                }
            });
            try
            {
                await foreach (var e in _api.StreamAsync(Agent, session.Token))
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
    /// <summary>The first event on a stream: the agent is registered and online.</summary>
    public const string Registered = "registered";
    /// <summary>A message for the agent.</summary>
    public const string Message = "message";
    /// <summary>A delivery receipt for a message the agent sent.</summary>
    public const string Receipt = "receipt";
    /// <summary>A message in a room the agent is a member of.</summary>
    public const string RoomMessage = "room-message";
    /// <summary>The agent was invited to a room (join it, or ignore the invitation).</summary>
    public const string RoomInvite = "room-invite";
    /// <summary>A room the agent is in changed: someone joined or left, or it collapsed.</summary>
    public const string RoomChanged = "room-changed";
    /// <summary>A job this machine meets the requirements of became ready.</summary>
    public const string JobReady = "job-ready";
    /// <summary>A job the agent posted or holds changed: claimed, moved, commented on, or its lease ran out.</summary>
    public const string JobChanged = "job-changed";
    /// <summary>A file receipt (downloaded, download-failed) for a file the agent sent.</summary>
    public const string FileReceipt = "file-receipt";
}

/// <summary>A message as an agent sees it, decrypted. <see cref="From"/> and <see cref="To"/> are <c>machine-name/agent</c>.</summary>
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

public sealed record LocalEvent(string Kind, LocalMessage? Message = null, LocalReceipt? Receipt = null, LocalFileReceipt? FileReceipt = null,
    LocalRoom? Room = null, LocalRoomInvite? Invite = null, LocalJob? Job = null);

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

/// <summary>A conversation as one local agent sees it; <see cref="Peer"/> is <c>machine-name/agent</c>, <see cref="State"/> open or finished.</summary>
public sealed record LocalConversation(Guid Id, string Peer, string? Subject, string StartedBy, DateTimeOffset StartedOn, DateTimeOffset LastActivityOn,
    string State);

/// <summary>Log filters; null means any. <see cref="Peer"/> is a machine name or id. Bodies are decrypted only with <see cref="Bodies"/>.</summary>
public sealed record LocalLogQuery(Guid? Conversation = null, Guid? Operation = null, string? Peer = null, string? Agent = null,
    DateTimeOffset? Since = null, int? Limit = null, bool Automatic = false, bool Bodies = false);

/// <summary>
/// One message in the log. <see cref="Body"/> is null unless bodies were asked for; <see cref="BodyDropped"/>
/// says retention dropped the envelope, so there's no body left to show.
/// </summary>
public sealed record LocalLogEntry(Guid Id, string From, string To, string Direction, string Type, Guid ConversationId, Guid? ReplyTo, Guid OperationId,
    string? Status, string? Subject, string ContentType, long Sequence, DateTimeOffset CreatedOn, DateTimeOffset StoredOn, DateTimeOffset ExpiresOn,
    string? State, string? StateDetail, bool BodyDropped, string? Body = null, IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>A message sent: to <c>machine-name/agent</c>; <see cref="State"/> is pushed, queued (by the router) or outbox (not relayed yet).</summary>
public sealed record LocalSent(Guid Id, string To, Guid ConversationId, long Sequence, DateTimeOffset ExpiresOn, string State);

public sealed record LocalAgentInfo(string Name, string State, DateTimeOffset ChangedOn, DateTimeOffset? RetiredOn);

public sealed record LocalPeerAgent(string Name, string State, DateTimeOffset ChangedOn);

/// <summary>An approved machine and its announced agents (all offline when the machine is).</summary>
public sealed record LocalMachine(string Id, string Name, bool Online, DateTimeOffset? LastSeenOn, string OperatingSystem,
    IReadOnlyList<LocalPeerAgent> Agents, MachineCapabilities? Capabilities = null);

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
    IReadOnlyList<LocalAgentInfo> Agents,
    IReadOnlyList<ClientMetric> Metrics);

public sealed record LocalError(string Error);

public sealed record LocalJoinRequest(string Name, string? Notes);

/// <summary>Where the machine stands after asking to join; compare <see cref="JoinCode"/> on the router before approving.</summary>
public sealed record LocalJoinResult(string Membership, string? Name, string MachineId, string JoinCode);

public sealed record LocalOk(bool Ok);

/// <summary>
/// A room as this machine knows it. Members are <c>machine-name/agent</c>; <see cref="Invited"/> are open invitations.
/// A collapsed room's history is still readable here.
/// </summary>
public sealed record LocalRoom(Guid Id, string Name, string Creator, DateTimeOffset OpenedOn, IReadOnlyList<string> Members,
    IReadOnlyList<LocalRoomInvite> Invited, bool Collapsed, string? CollapseReason);

/// <summary>An open invitation: <see cref="Invitee"/> joins with <c>room join</c> before <see cref="ExpiresOn"/>, or ignores it.</summary>
public sealed record LocalRoomInvite(Guid RoomId, string Room, string Invitee, string InvitedBy, DateTimeOffset ExpiresOn);

public sealed record LocalRoomCreate(string Agent, string Name, IReadOnlyList<string>? Invite = null);

/// <summary>Invite agents (addresses may be <c>machine/agent</c> or groups, resolved now).</summary>
public sealed record LocalRoomInviteRequest(string Agent, IReadOnlyList<string> Invite);

/// <summary>Join (accept an invitation) or leave.</summary>
public sealed record LocalRoomAgent(string Agent);

/// <summary>A job on the board. Poster and claimant are <c>machine-name/agent</c>; the board is open to every approved machine.</summary>
/// <param name="Requirements">What a machine needs to claim it (e.g. memory&gt;=32); <paramref name="Unmet"/> is what this machine lacks.</param>
public sealed record LocalJob(Guid Id, string Title, string Description, IReadOnlyList<string> Requirements, IReadOnlyList<string> Unmet, string Poster,
    string Column, string? Claimant,
    DateTimeOffset? LeaseExpiresOn, string? Result, DateTimeOffset PostedOn, DateTimeOffset UpdatedOn, IReadOnlyList<LocalJobEntry> History);

public sealed record LocalJobEntry(DateTimeOffset OccurredOn, string By, string What, string? Column, string? Text);

/// <summary>
/// Post a job: to ready (default) or backlog. Only a machine meeting every requirement (<c>memory&gt;=32</c>, <c>memory&lt;=8</c>,
/// <c>os=linux</c>, <c>gpu~rtx</c>, ...) may claim it.
/// </summary>
public sealed record LocalJobPost(string Agent, string Title, string? Description = null, IReadOnlyList<string>? Requirements = null, bool Backlog = false);

/// <summary>This machine: what it has, as detected and reported to the router.</summary>
public sealed record LocalCapabilities(MachineCapabilities Capabilities, string Summary);

/// <summary>Claim, release, cancel, comment on (<see cref="Text"/>) or move (<see cref="Column"/>, with a result note for done and failed) a job.</summary>
public sealed record LocalJobAction(string Agent, string? Column = null, string? Text = null);

public sealed record LocalRoomSend(string Agent, string Body, Guid? ReplyTo = null, string? Subject = null, string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// The owner as this machine sees it: the router's key, the key pinned here (only that one's signatures count), and
/// the desk. <see cref="HereLifetime"/> is how long a <c>here</c> lasts (Daemon:HereLifetime).
/// </summary>
public sealed record LocalOwner(string MachineId, string? RouterId, string? RouterKey, string? RouterFingerprint, string? PinnedKey, string? PinnedFingerprint,
    string? DeskMachine, string? DeskName, DateTimeOffset? DeskUntil, bool IsDesk, TimeSpan HereLifetime);

public sealed record LocalOwnerPin(string Key);

public sealed record LocalAway(HereClaim? Signed);

/// <summary>An agent asks: <see cref="Tool"/> and <see cref="Command"/> (a shell command) when it's about running one.</summary>
/// <remarks><see cref="Id"/>: chosen by the asker, so it can withdraw even if it gives up before the answer to the ask arrives; asking again with it is a no-op.</remarks>
public sealed record LocalApprovalRequest(string Agent, string Summary, string? Details = null, string? Tool = null, string? Command = null, Guid? Id = null);

/// <summary>An approval: <see cref="From"/> is "machine-name/agent". <see cref="AnsweredBy"/>: "rule", or the desk machine's name.</summary>
/// <summary>An approval. Direction "join": a machine asking to join (<see cref="Command"/> is its join code), answered with admin approve.</summary>
public sealed record LocalApproval(Guid Id, string Direction, string From, string Summary, string? Details, string? Tool, string? Command, string State,
    bool Always, string? Reason, DateTimeOffset RequestedOn, DateTimeOffset? AnsweredOn, string? AnsweredBy, string? Desk);

public sealed record LocalAnswer(bool Always = false, string? Reason = null);
