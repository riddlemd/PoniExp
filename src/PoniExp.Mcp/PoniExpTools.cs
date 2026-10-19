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
        [Description("Recipients: 'machine' (its default agent), 'machine/agent', 'role:<role>' (any one agent with that role), " +
                     "'role:<role>/all' (every one) or 'group:<name>' (the owner's list).")] string[] to,
        [Description("The message text.")] string body,
        [Description("A short title (optional).")] string? subject = null,
        [Description("Continue this conversation (one recipient only).")] string? conversationId = null,
        [Description("How the body is to be read (default text/plain).")] string? contentType = null,
        [Description("Free-form key/value metadata for agent protocols.")] Dictionary<string, string>? metadata = null,
        [Description("How long it lives, e.g. '30m', '2h', '3d' (default and maximum are the machine's settings).")] string? expiresIn = null,
        [Description("To retry a request safely, send it again with the first message's operationId (one recipient only): the other side " +
                     "won't do it twice, and answers with 'still running' or the result it already gave.")] string? operationId = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.SendAsync(new LocalSendRequest(me.Agent, to, body, subject, ParseId(conversationId), OperationId: ParseId(operationId),
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
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.SendAsync(new LocalSendRequest(me.Agent, to, body, subject, ParseId(conversationId),
            Attachments: [.. files.Select(Path.GetFullPath)]), cancellationToken));

    [McpServerTool(Name = "fetch"), Description(
        "Download a message's files to this machine, verified against the message, and get their local paths. Fetching once is enough " +
        "even if other agents here got the same file. Call 'release' when you are done with them.")]
    public async Task<string> FetchAsync([Description("The id of the message with the files.")] string messageId,
        [Description("Just this file (its blobId); default all.")] string? blobId = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.FetchAsync(me.Agent, new LocalFileRequest(ParseId(messageId) ?? throw new ArgumentException("messageId is required"),
            ParseId(blobId)), cancellationToken));

    [McpServerTool(Name = "release"), Description(
        "You are done with a message's files: the local copy is deleted once every agent here that received them has released them. " +
        "Acknowledging a message doesn't release its files.")]
    public async Task<string> ReleaseAsync([Description("The id of the message with the files.")] string messageId,
        [Description("Just this file (its blobId); default all.")] string? blobId = null, CancellationToken cancellationToken = default) =>
        Json(await me.Api.ReleaseAsync(me.Agent, new LocalFileRequest(ParseId(messageId) ?? throw new ArgumentException("messageId is required"),
            ParseId(blobId)), cancellationToken));

    [McpServerTool(Name = "reply"), Description(
        "Reply to a message you received, in its conversation, to its sender. Replying also acknowledges it. " +
        "Use status 'progress' for an update or a question, 'done' when the work is finished, 'failed' if it can't be done.")]
    public async Task<string> ReplyAsync(
        [Description("The id of the message you are answering.")] string messageId,
        [Description("The reply text.")] string body,
        [Description("progress, done or failed (optional).")] string? status = null,
        [Description("Free-form key/value metadata.")] Dictionary<string, string>? metadata = null,
        [Description("Paths of files to send back (optional).")] string[]? files = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.ReplyAsync(me.Agent, new LocalReplyRequest(ParseId(messageId) ?? throw new ArgumentException("messageId is required"),
            body, status, Metadata: metadata, Attachments: files is { Length: > 0 } ? [.. files.Select(Path.GetFullPath)] : null), cancellationToken));

    [McpServerTool(Name = "ack"), Description(
        "Acknowledge a message: tell its sender you have taken it on. Do this when you start on it (replying acknowledges too).")]
    public async Task<string> AckAsync([Description("The message id.")] string messageId, CancellationToken cancellationToken = default) =>
        Json(await me.Api.AckAsync(me.Agent, ParseId(messageId) ?? throw new ArgumentException("messageId is required"), cancellationToken));

    [McpServerTool(Name = "inbox", ReadOnly = true), Description(
        "Messages for you that you haven't acknowledged yet, oldest first. Fetching doesn't acknowledge them.")]
    public async Task<string> InboxAsync(CancellationToken cancellationToken = default) =>
        Json(await me.Api.InboxAsync(me.Agent, cancellationToken));

    [McpServerTool(Name = "conversations", ReadOnly = true), Description(
        "Your conversations, most recent first: who with, the subject, and whether each is open or finished (its last reply was done or failed). " +
        "Use it to pick up where you left off after a restart, then 'conversation' for the messages.")]
    public async Task<string> ConversationsAsync(CancellationToken cancellationToken = default) =>
        Json(await me.Api.ConversationsAsync(me.Agent, cancellationToken));

    [McpServerTool(Name = "conversation", ReadOnly = true), Description("Every message you sent and received in a conversation, oldest first.")]
    public async Task<string> ConversationAsync([Description("The conversation id.")] string conversationId, CancellationToken cancellationToken = default) =>
        Json(await me.Api.ConversationAsync(me.Agent, ParseId(conversationId) ?? throw new ArgumentException("conversationId is required"),
            cancellationToken));

    [McpServerTool(Name = "message", ReadOnly = true), Description(
        "One message you sent or received, with its delivery history (for a message you sent: relayed, received, acknowledged, or rejected).")]
    public async Task<string> MessageAsync([Description("The message id.")] string messageId, CancellationToken cancellationToken = default) =>
        Json(await me.Api.MessageAsync(me.Agent, ParseId(messageId) ?? throw new ArgumentException("messageId is required"), cancellationToken));

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
        [Description("Agents to invite: 'machine/agent', 'role:<role>' or 'role:<role>/all', or 'group:<name>'.")] string[]? invite = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.CreateRoomAsync(new LocalRoomCreate(me.Agent, name, invite), cancellationToken));

    [McpServerTool(Name = "room_invite"), Description("Invite agents to a room you're in. They must accept (room_join).")]
    public async Task<string> RoomInviteAsync([Description("The room's name or id.")] string room,
        [Description("Agents to invite: 'machine/agent', 'role:<role>' or 'role:<role>/all', or 'group:<name>'.")] string[] invite,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.InviteToRoomAsync(room, new LocalRoomInviteRequest(me.Agent, invite), cancellationToken));

    [McpServerTool(Name = "room_join"), Description("Accept an invitation to a room. You see what's said from now on, not before.")]
    public async Task<string> RoomJoinAsync([Description("The room's name or id.")] string room, CancellationToken cancellationToken = default) =>
        Json(await me.Api.JoinRoomAsync(room, me.Agent, cancellationToken));

    [McpServerTool(Name = "room_leave"), Description("Leave a room when you no longer need to be part of the conversation.")]
    public async Task<string> RoomLeaveAsync([Description("The room's name or id.")] string room, CancellationToken cancellationToken = default) =>
        Json(await me.Api.LeaveRoomAsync(room, me.Agent, cancellationToken));

    [McpServerTool(Name = "machines", ReadOnly = true), Description("The other machines, whether they're online, their agents and the agents' states.")]
    public async Task<string> MachinesAsync(CancellationToken cancellationToken = default) => Json(await me.Api.MachinesAsync(cancellationToken));

    [McpServerTool(Name = "status", ReadOnly = true), Description("This machine: who it is, whether it's connected, its agents, and message metrics.")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken = default) =>
        Json(new { agent = me.Agent, machine = await me.Api.StatusAsync(cancellationToken) });

    private static string Json(object value) => JsonSerializer.Serialize(value, WireProtocol.Json);

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
    public static async Task RunAsync(LocalApiClient api, string agent, CancellationToken cancellationToken, IReadOnlyList<string>? roles = null)
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
            onError: e => Console.Error.WriteLine($"poniexp: agent connection: {e.Message}"), roles: roles);
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
