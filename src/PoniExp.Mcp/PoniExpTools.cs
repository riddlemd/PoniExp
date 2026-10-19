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

/// <summary>Which endpoint this MCP server is, and the daemon it talks to.</summary>
public sealed record McpEndpoint(LocalApiClient Api, string Endpoint);

/// <summary>
/// The MCP tools: an agent's view of PoniExp, as one endpoint on this machine. Every result is JSON.
/// Fetching the inbox doesn't acknowledge anything: the agent acknowledges with <c>ack</c>, or by replying.
/// </summary>
[McpServerToolType]
public sealed class PoniExpTools(McpEndpoint me)
{
    [McpServerTool(Name = "send"), Description(
        "Send a message to one or more agents. Each recipient gets its own message (and starts its own conversation " +
        "unless conversationId is given, which needs a single recipient). Recipients are 'machine' or 'machine/endpoint'.")]
    public async Task<string> SendAsync(
        [Description("Recipients: 'machine' (its default endpoint) or 'machine/endpoint'.")] string[] to,
        [Description("The message text.")] string body,
        [Description("A short title (optional).")] string? subject = null,
        [Description("Continue this conversation (one recipient only).")] string? conversationId = null,
        [Description("How the body is to be read (default text/plain).")] string? contentType = null,
        [Description("Free-form key/value metadata for agent protocols.")] Dictionary<string, string>? metadata = null,
        [Description("How long it lives, e.g. '30m', '2h', '3d' (default and maximum are the machine's settings).")] string? expiresIn = null,
        [Description("To retry a request safely, send it again with the first message's operationId (one recipient only): the other side " +
                     "won't do it twice, and answers with 'still running' or the result it already gave.")] string? operationId = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.SendAsync(new LocalSendRequest(me.Endpoint, to, body, subject, ParseId(conversationId), OperationId: ParseId(operationId),
            ContentType: contentType, Metadata: metadata, ExpiresIn: ParseDuration(expiresIn)), cancellationToken));

    [McpServerTool(Name = "reply"), Description(
        "Reply to a message you received, in its conversation, to its sender. Replying also acknowledges it. " +
        "Use status 'progress' for an update or a question, 'done' when the work is finished, 'failed' if it can't be done.")]
    public async Task<string> ReplyAsync(
        [Description("The id of the message you are answering.")] string messageId,
        [Description("The reply text.")] string body,
        [Description("progress, done or failed (optional).")] string? status = null,
        [Description("Free-form key/value metadata.")] Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        Json(await me.Api.ReplyAsync(me.Endpoint, new LocalReplyRequest(ParseId(messageId) ?? throw new ArgumentException("messageId is required"),
            body, status, Metadata: metadata), cancellationToken));

    [McpServerTool(Name = "ack"), Description(
        "Acknowledge a message: tell its sender you have taken it on. Do this when you start on it (replying acknowledges too).")]
    public async Task<string> AckAsync([Description("The message id.")] string messageId, CancellationToken cancellationToken = default) =>
        Json(await me.Api.AckAsync(me.Endpoint, ParseId(messageId) ?? throw new ArgumentException("messageId is required"), cancellationToken));

    [McpServerTool(Name = "inbox", ReadOnly = true), Description(
        "Messages for you that you haven't acknowledged yet, oldest first. Fetching doesn't acknowledge them.")]
    public async Task<string> InboxAsync(CancellationToken cancellationToken = default) =>
        Json(await me.Api.InboxAsync(me.Endpoint, cancellationToken));

    [McpServerTool(Name = "conversations", ReadOnly = true), Description(
        "Your conversations, most recent first: who with, the subject, and whether each is open or finished (its last reply was done or failed). " +
        "Use it to pick up where you left off after a restart, then 'conversation' for the messages.")]
    public async Task<string> ConversationsAsync(CancellationToken cancellationToken = default) =>
        Json(await me.Api.ConversationsAsync(me.Endpoint, cancellationToken));

    [McpServerTool(Name = "conversation", ReadOnly = true), Description("Every message you sent and received in a conversation, oldest first.")]
    public async Task<string> ConversationAsync([Description("The conversation id.")] string conversationId, CancellationToken cancellationToken = default) =>
        Json(await me.Api.ConversationAsync(me.Endpoint, ParseId(conversationId) ?? throw new ArgumentException("conversationId is required"),
            cancellationToken));

    [McpServerTool(Name = "message", ReadOnly = true), Description(
        "One message you sent or received, with its delivery history (for a message you sent: relayed, received, acknowledged, or rejected).")]
    public async Task<string> MessageAsync([Description("The message id.")] string messageId, CancellationToken cancellationToken = default) =>
        Json(await me.Api.MessageAsync(me.Endpoint, ParseId(messageId) ?? throw new ArgumentException("messageId is required"), cancellationToken));

    [McpServerTool(Name = "machines", ReadOnly = true), Description("The other machines, whether they're online, and their endpoints (agents) and their states.")]
    public async Task<string> MachinesAsync(CancellationToken cancellationToken = default) => Json(await me.Api.MachinesAsync(cancellationToken));

    [McpServerTool(Name = "status", ReadOnly = true), Description("This machine: who it is, whether it's connected, its endpoints, and message metrics.")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken = default) =>
        Json(new { endpoint = me.Endpoint, machine = await me.Api.StatusAsync(cancellationToken) });

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
    /// Runs the MCP server on stdin/stdout as <paramref name="endpoint"/>, registered with the daemon (online)
    /// for as long as it runs. Logs go to stderr: stdout is the protocol.
    /// </summary>
    public static async Task RunAsync(LocalApiClient api, string endpoint, CancellationToken cancellationToken)
    {
        if (!Limits.IsValidEndpoint(endpoint) || endpoint == Address.DaemonEndpoint)
        {
            throw new ArgumentException($"an endpoint must be {Limits.EndpointRule}, and not '{Address.DaemonEndpoint}'");
        }

        if (!await api.IsRunningAsync(cancellationToken))
        {
            throw new InvalidOperationException("the daemon isn't running; start it with 'poniexp-client daemon' (or 'service install')");
        }

        await using var connection = new EndpointConnection(api, endpoint, _ => Task.CompletedTask,
            onError: e => Console.Error.WriteLine($"poniexp: endpoint connection: {e.Message}"));
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(new McpEndpoint(api, endpoint));
        builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "poniexp", Version = WireProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .WithStdioServerTransport()
            .WithTools<PoniExpTools>();
        await builder.Build().RunAsync(cancellationToken);
    }
}
