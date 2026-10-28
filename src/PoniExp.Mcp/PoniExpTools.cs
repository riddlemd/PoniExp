using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Mcp;

/// <summary>Which agent this MCP server is, and the daemon it talks to.</summary>
public sealed record McpAgent(LocalApiClient Api, string Agent);

/// <summary>What the MCP <c>ack</c> tool returns: no body, metadata or attachments.</summary>
public sealed record AckResult(Guid Id, string? State, DateTimeOffset? ChangedOn);

/// <summary>
/// The MCP tools: an agent's view of PoniExp, as one agent on this machine. Every result is JSON.
/// Fetching the inbox doesn't acknowledge anything: the agent acknowledges with <c>ack</c>, or by replying.
/// </summary>
[McpServerToolType]
public sealed class PoniExpTools(McpAgent me)
{
    [McpServerTool(Name = "send"), Description(
        "Send a message to one or more agents. Each recipient gets its own message (and starts its own conversation " +
        "unless conversationId is given, which needs a single recipient). Recipients are 'machine' or 'machine/agent'.")]
    public async Task<string> SendAsync(
        [Description("Recipients: 'machine' (its default agent), 'machine/agent', 'group:<name>' (the owner's list) or 'room:<name>'.")] string[] to,
        [Description("The message text.")] string body,
        [Description("A short title (optional).")] string? subject = null,
        [Description("Continue this conversation (one recipient only).")] string? conversationId = null,
        [Description("How the body is to be read (default text/plain).")] string? contentType = null,
        [Description("Free-form key/value metadata for agent protocols.")] Dictionary<string, string>? metadata = null,
        [Description("How long it lives, e.g. '30m', '2h', '3d' (default and maximum are the machine's settings).")] string? expiresIn = null,
        [Description("To retry a request safely, send it again with the first message's operationId (one recipient only): the other side " +
                     "won't do it twice, and answers with 'still running' or the result it already gave.")] string? operationId = null,
        [Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.SendAsync(new LocalSendRequest(As(agent), to, body, subject, ParseId(conversationId), OperationId: ParseId(operationId),
            ContentType: contentType, Metadata: metadata, ExpiresIn: ParseDuration(expiresIn)), cancellationToken));

    [McpServerTool(Name = "attach"), Description(
        "Send files with a message. Each file is encrypted and uploaded once, shared with exactly these recipients; each recipient gets " +
        "its own message. You'll get file receipts (downloaded, or download-failed with why) as they fetch them.")]
    public async Task<string> AttachAsync(
        [Description("Recipients: 'machine' or 'machine/agent'.")] string[] to,
        [Description("Paths of the files on this machine.")] string[] files,
        [Description("The message text.")] string body,
        [Description("A short title (optional).")] string? subject = null,
        [Description("Continue this conversation (one recipient only).")] string? conversationId = null,
        [Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.SendAsync(new LocalSendRequest(As(agent), to, body, subject, ParseId(conversationId),
            Attachments: [.. files.Select(Path.GetFullPath)]), cancellationToken));

    [McpServerTool(Name = "fetch"), Description(
        "Download a message's files to this machine, verified against the message, and get their local paths. Fetching once is enough " +
        "even if other agents here got the same file. Call 'release' when you are done with them.")]
    public async Task<string> FetchAsync([Description(MessageIdOrPrefix + " of the message with the files.")] string messageId,
        [Description("Just this file (its blobId); default all.")] string? blobId = null, [Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.FetchAsync(As(agent), new LocalFileRequest(Required(messageId, nameof(messageId)),
            ParseId(blobId)), cancellationToken));

    [McpServerTool(Name = "release"), Description(
        "You are done with a message's files: the local copy is deleted once every agent here that received them has released them. " +
        "Acknowledging a message doesn't release its files.")]
    public async Task<string> ReleaseAsync([Description(MessageIdOrPrefix + " of the message with the files.")] string messageId,
        [Description("Just this file (its blobId); default all.")] string? blobId = null, [Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.ReleaseAsync(As(agent), new LocalFileRequest(Required(messageId, nameof(messageId)),
            ParseId(blobId)), cancellationToken));

    [McpServerTool(Name = "reply"), Description(
        "Reply to a message you received, in its conversation, to its sender. Replying also acknowledges it. " +
        "Use status 'progress' for an update or a question, 'done' when the work is finished, 'failed' if it can't be done.")]
    public async Task<string> ReplyAsync(
        [Description(MessageIdOrPrefix + " of the message you are answering.")] string messageId,
        [Description("The reply text.")] string body,
        [Description("progress, done or failed (optional).")] string? status = null,
        [Description("Free-form key/value metadata.")] Dictionary<string, string>? metadata = null,
        [Description("Paths of files to send back (optional).")] string[]? files = null,
        [Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.ReplyAsync(As(agent), new LocalReplyRequest(Required(messageId, nameof(messageId)),
            body, status, Metadata: metadata, Attachments: files is { Length: > 0 } ? [.. files.Select(Path.GetFullPath)] : null), cancellationToken));

    [McpServerTool(Name = "ack"), Description(
        "Acknowledge a message: tell its sender you have taken it on. Do this when you start on it (replying acknowledges too). " +
        "Returns only the id, state and when it was acknowledged; acknowledging again returns the same.")]
    public async Task<string> AckAsync([Description(MessageIdOrPrefix + ".")] string messageId, [Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default)
    {
        var acked = await me.Api.AckAsync(As(agent), Required(messageId, nameof(messageId)), cancellationToken);
        // The caller read the message from inbox already: echoing the body back would cost its tokens twice.
        var detail = await me.Api.MessageAsync(As(agent), acked.Id.ToString(), cancellationToken);
        var changedOn = detail.Events.LastOrDefault(e => e.Event == MessageEvents.Acknowledged)?.OccurredOn;
        return Json(new AckResult(acked.Id, acked.State, changedOn));
    }

    [McpServerTool(Name = "inbox", ReadOnly = true), Description(
        "Messages for you that you haven't acknowledged yet, oldest first. Fetching doesn't acknowledge them.")]
    public async Task<string> InboxAsync([Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.InboxAsync(As(agent), cancellationToken));

    [McpServerTool(Name = "conversations", ReadOnly = true), Description(
        "Your conversations, most recent first: who with, the subject, and whether each is open or finished (its last reply was done or failed). " +
        "Use it to pick up where you left off after a restart, then 'conversation' for the messages.")]
    public async Task<string> ConversationsAsync([Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.ConversationsAsync(As(agent), cancellationToken));

    [McpServerTool(Name = "conversation", ReadOnly = true), Description("Every message you sent and received in a conversation, oldest first.")]
    public async Task<string> ConversationAsync([Description("The conversation id, or a unique start of it (4 characters or more).")] string conversationId,
        [Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.ConversationAsync(As(agent), Required(conversationId, nameof(conversationId)), cancellationToken));

    [McpServerTool(Name = "message", ReadOnly = true), Description(
        "One message you sent or received, with its delivery history (for a message you sent: relayed, received, acknowledged, or rejected).")]
    public async Task<string> MessageAsync([Description(MessageIdOrPrefix + ".")] string messageId, [Description(AgentParameter)] string? agent = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.MessageAsync(As(agent), Required(messageId, nameof(messageId)), cancellationToken));

    [McpServerTool(Name = "jobs", ReadOnly = true), Description(
        "The job board: work any agent on any approved machine posted, open for all to see (don't put secrets on it). " +
        "Lists unfinished jobs (all=true adds finished ones), optionally one column: backlog, ready, claimed, running, done, failed.")]
    public async Task<string> JobsAsync(bool all = false, string? column = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.JobsAsync(all, column, cancellationToken));

    [McpServerTool(Name = "capabilities", ReadOnly = true), Description(
        "What this machine has (OS, CPU, cores, memory, GPUs, tags), which job requirements are checked against. " +
        "The 'jobs' list shows for each job what this machine doesn't meet ('unmet').")]
    public async Task<string> CapabilitiesAsync(CancellationToken cancellationToken = default) => Json(await me.Api.CapabilitiesAsync(cancellationToken));

    [McpServerTool(Name = "job", ReadOnly = true), Description("One job, with its description, result and history (moves and comments).")]
    public async Task<string> JobAsync([Description("The job's id (or the start of it).")] string id, CancellationToken cancellationToken = default) =>
        Json(await me.Api.JobAsync(id, cancellationToken));

    [McpServerTool(Name = "job_post"), Description(
        "Post a job to the board for any agent to pick up. With requirements, only machines meeting all of them may claim it: " +
        "e.g. 'os=linux', 'memory>=32', 'memory<=8' (to test on small machines), 'cores>=8', 'gpu~rtx', 'gpu-memory>=16', 'tag=docker'. " +
        "It goes to 'ready' (or 'backlog' if it isn't ready to be picked up yet).")]
    public async Task<string> JobPostAsync([Description("A one-line title.")] string title, [Description("What to do (markdown).")] string? description = null,
        [Description("Requirements on the claiming machine: key op value, keys os, os-version, arch, cpu, cores, memory (GB), gpu, gpu-memory (GB), " +
                     "gpus, tag; ops =, !=, >=, <=, ~ (contains).")] string[]? requirements = null, bool backlog = false,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.PostJobAsync(new LocalJobPost(me.Agent, title, description, requirements, backlog), cancellationToken));

    [McpServerTool(Name = "job_claim"), Description(
        "Claim a ready job when you have capacity: it's yours while you stay connected. Move it to 'running' when you start, " +
        "then 'done' or 'failed' with a result note; or release it if you can't do it.")]
    public async Task<string> JobClaimAsync([Description("The job's id (or the start of it).")] string id, CancellationToken cancellationToken = default) =>
        Json(await me.Api.JobActionAsync(id, "claim", new LocalJobAction(me.Agent), cancellationToken));

    [McpServerTool(Name = "job_move"), Description(
        "Move a job: between backlog and ready (anyone), or a job you claimed to running, done or failed (with a result note for those).")]
    public async Task<string> JobMoveAsync([Description("The job's id (or the start of it).")] string id,
        [Description("backlog, ready, running, done or failed.")] string column, [Description("The result, for done or failed.")] string? note = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.JobActionAsync(id, "move", new LocalJobAction(me.Agent, column, note), cancellationToken));

    [McpServerTool(Name = "job_release"), Description("Give back a job you claimed: it's ready for someone else.")]
    public async Task<string> JobReleaseAsync([Description("The job's id (or the start of it).")] string id, [Description("Why (optional).")] string? note = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.JobActionAsync(id, "release", new LocalJobAction(me.Agent, Text: note), cancellationToken));

    [McpServerTool(Name = "job_comment"), Description("Comment on a job (everyone sees it).")]
    public async Task<string> JobCommentAsync([Description("The job's id (or the start of it).")] string id, string text, CancellationToken cancellationToken = default) =>
        Json(await me.Api.JobActionAsync(id, "comment", new LocalJobAction(me.Agent, Text: text), cancellationToken));

    [McpServerTool(Name = "job_cancel"), Description("Cancel a job you posted that hasn't finished.")]
    public async Task<string> JobCancelAsync([Description("The job's id (or the start of it).")] string id, CancellationToken cancellationToken = default) =>
        Json(await me.Api.JobActionAsync(id, "cancel", new LocalJobAction(me.Agent), cancellationToken));

    [McpServerTool(Name = "rooms", ReadOnly = true), Description(
        "Rooms: named, standing conversations where every member sees every message. Lists the rooms you're in or invited to, " +
        "with their members and open invitations.")]
    public async Task<string> RoomsAsync(CancellationToken cancellationToken = default) => Json(await me.Api.RoomsAsync(me.Agent, cancellationToken));

    [McpServerTool(Name = "room_invites", ReadOnly = true), Description(
        "Your open room invitations. Join one with room_join, or ignore it (it expires).")]
    public async Task<string> RoomInvitesAsync(CancellationToken cancellationToken = default) =>
        Json(await me.Api.RoomInvitesAsync(me.Agent, cancellationToken));

    [McpServerTool(Name = "room", ReadOnly = true), Description(
        "Read a room's messages, oldest first: those said while you were in it (the most recent 200, or those after 'since'). " +
        "New ones also arrive as you go; rooms have no ack and no inbox.")]
    public async Task<string> RoomAsync([Description("The room's name or id.")] string room,
        [Description("Only messages created after this time (ISO 8601), e.g. the last one you saw.")] string? since = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.ReadRoomAsync(room, me.Agent, since is null ? null : DateTimeOffset.Parse(since, System.Globalization.CultureInfo.InvariantCulture),
            ct: cancellationToken));

    [McpServerTool(Name = "room_send"), Description("Say something in a room you're in: every member sees it. Set replyTo to answer a message in it.")]
    public async Task<string> RoomSendAsync([Description("The room's name or id.")] string room, [Description("The message text.")] string body,
        [Description("The id of the message you are answering (optional).")] string? replyTo = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.SendToRoomAsync(room, new LocalRoomSend(me.Agent, body, ParseId(replyTo)), cancellationToken));

    [McpServerTool(Name = "room_create"), Description(
        "Open a room with you in it. Its name must be free among live rooms. Anyone invited must accept; members only ever " +
        "leave (nobody can remove anyone). The room collapses when the last member leaves or it goes quiet for long enough.")]
    public async Task<string> RoomCreateAsync([Description("The room's name: lowercase letters, digits, '.', '_' or '-'.")] string name,
        [Description("Agents to invite: 'machine/agent' or 'group:<name>'.")] string[]? invite = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.CreateRoomAsync(new LocalRoomCreate(me.Agent, name, invite), cancellationToken));

    [McpServerTool(Name = "room_invite"), Description("Invite agents to a room you're in. They must accept (room_join).")]
    public async Task<string> RoomInviteAsync([Description("The room's name or id.")] string room,
        [Description("Agents to invite: 'machine/agent' or 'group:<name>'.")] string[] invite,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.InviteToRoomAsync(room, new LocalRoomInviteRequest(me.Agent, invite), cancellationToken));

    [McpServerTool(Name = "room_join"), Description("Accept an invitation to a room. You see what's said from now on, not before.")]
    public async Task<string> RoomJoinAsync([Description("The room's name or id.")] string room, CancellationToken cancellationToken = default) =>
        Json(await me.Api.JoinRoomAsync(room, me.Agent, cancellationToken));

    [McpServerTool(Name = "room_leave"), Description("Leave a room when you no longer need to be part of the conversation.")]
    public async Task<string> RoomLeaveAsync([Description("The room's name or id.")] string room, CancellationToken cancellationToken = default) =>
        Json(await me.Api.LeaveRoomAsync(room, me.Agent, cancellationToken));

    [McpServerTool(Name = "request_approval"), Description(
        "Ask the owner's permission before doing something, and wait for the answer. It goes to wherever the owner is working (their " +
        "desk); with no desk it waits until there is one. Returns state 'approved' or 'denied' (with the owner's reason).")]
    public async Task<string> RequestApprovalAsync(
        [Description("What you want to do, in one or two sentences.")] string summary,
        [Description("Detail the owner may want: what, why, what it touches (optional).")] string? details = null,
        [Description("The exact shell command, when it's about running one (optional; 'always allow' then covers only that command).")] string? command = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.AskAsync(new LocalApprovalRequest(me.Agent, summary, details, command is null ? null : "shell", command), cancellationToken));

    /// <summary>
    /// Claude Code's permission prompt tool (<c>claude --permission-prompt-tool mcp__poniexp__approve</c>): its permission prompts go to
    /// the owner's desk. Returns the payload Claude Code expects: allow with the input unchanged, or deny with the reason.
    /// </summary>
    [McpServerTool(Name = "approve"), Description(
        "Permission prompts for Claude Code (run it with --permission-prompt-tool mcp__poniexp__approve); not for calling directly.")]
    public async Task<string> ApproveAsync(
        [Description("The tool asking for permission.")] string tool_name,
        [Description("The tool's input.")] JsonElement input,
        [Description("The tool call's id.")] string? tool_use_id = null,
        CancellationToken cancellationToken = default)
    {
        var command = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var compact = input.GetRawText();
        var summary = $"{tool_name}: {command ?? (compact.Length > 300 ? compact[..300] + "..." : compact)}";
        var details = JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true });
        var answer = await me.Api.AskAsync(new LocalApprovalRequest(me.Agent, summary.Length > 1900 ? summary[..1900] + "..." : summary, details, tool_name, command),
            cancellationToken);
        return answer.State == ApprovalRecord.Approved
            ? JsonSerializer.Serialize(new { behavior = "allow", updatedInput = input })
            : JsonSerializer.Serialize(new { behavior = "deny", message = answer.Reason is { } why ? $"The owner denied it: {why}" : "The owner denied it." });
    }

    [McpServerTool(Name = "machines", ReadOnly = true), Description("The other machines, whether they're online, their agents and the agents' states.")]
    public async Task<string> MachinesAsync(CancellationToken cancellationToken = default) => Json(await me.Api.MachinesAsync(cancellationToken));

    [McpServerTool(Name = "status", ReadOnly = true), Description("This machine: who it is, whether it's connected, its agents, and message metrics.")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken = default) =>
        Json(new { agent = me.Agent, machine = await me.Api.StatusAsync(cancellationToken) });

    private static string Json(object value) => JsonSerializer.Serialize(value, WireProtocol.Json);

    private const string AgentParameter =
        "Act as this session's own agent (e.g. 'claude.8b1d44e2') when a PoniExp nudge or announcement names one; omit it otherwise.";

    /// <summary>The agent a call acts as: this server's, or one of its session agents (never an unrelated one).</summary>
    internal string As(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return me.Agent;
        }

        agent = agent.Trim();
        return SessionWake.IsSelfOrSession(me.Agent, agent)
            ? agent
            : throw new ArgumentException($"agent '{agent}' isn't '{me.Agent}' or one of its sessions ('{me.Agent}.<id>')");
    }

    /// <summary>Message ids are resolved by the daemon: the whole id, or a prefix that matches only one of your messages.</summary>
    private const string MessageIdOrPrefix = "The message id, or a unique start of it (4 characters or more)";

    private static string Required(string? text, string name) =>
        string.IsNullOrWhiteSpace(text) ? throw new ArgumentException($"{name} is required") : text.Trim();

    private static Guid? ParseId(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Guid.TryParse(text, out var id) ? id : throw new ArgumentException($"'{text}' isn't an id");

    internal static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length > 1 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            switch (text[^1])
            {
                case 's': return TimeSpan.FromSeconds(amount);
                case 'm': return TimeSpan.FromMinutes(amount);
                case 'h': return TimeSpan.FromHours(amount);
                case 'd': return TimeSpan.FromDays(amount);
            }
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span)
            ? span
            : throw new ArgumentException($"'{text}' isn't a duration like 30s, 10m, 2h or 3d");
    }
}

public static class McpServer
{
    /// <summary>
    /// Runs the MCP server on stdin/stdout as <paramref name="agent"/>, registered with the daemon (online)
    /// for as long as it runs. Logs go to stderr: stdout is the protocol.
    /// </summary>
    public static async Task RunAsync(LocalApiClient api, string agent, CancellationToken cancellationToken)
    {
        if (!Limits.IsValidAgent(agent) || agent is Address.DaemonAgent or RoomAddress.Agent)
        {
            throw new ArgumentException($"an agent must be {Limits.AgentRule}, and not '{Address.DaemonAgent}'");
        }

        if (!await api.IsRunningAsync(cancellationToken))
        {
            throw new InvalidOperationException("the daemon isn't running; start it with 'poniexp-client daemon' (or 'service install')");
        }

        await using var connection = new AgentConnection(api, agent, _ => Task.CompletedTask,
            onError: e => Console.Error.WriteLine($"poniexp: agent connection: {e.Message}"));
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(new McpAgent(api, agent));
        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "poniexp", Version = WireProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .WithStdioServerTransport()
            .WithTools<PoniExpTools>();
        await builder.Build().RunAsync(cancellationToken);
    }
}
