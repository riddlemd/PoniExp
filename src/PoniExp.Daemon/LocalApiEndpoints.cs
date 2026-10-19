using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// The local API (HTTP over the daemon's pipe or socket). See <see cref="LocalApiClient"/> for the calls.
/// Each agent is shown only its own messages: scoping for convenience, not a security boundary.
/// </summary>
public static class LocalApiEndpoints
{
    private static LocalLogEntry ToLog(Daemon d, LogEntry e, bool bodies)
    {
        var m = e.Message;
        var incoming = m.ToMachine == d.MachineId && m.FromMachine != d.MachineId;
        MessageContent? content = null;
        if (bodies && m.Envelope is { } envelope)
        {
            try
            {
                content = d.Relay.Open(envelope);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
            {
                // Shown without its body; the metadata stands.
            }
        }

        return new LocalLogEntry(m.Id, $"{d.NameOf(m.FromMachine)}/{m.FromAgent}", $"{d.NameOf(m.ToMachine)}/{m.ToAgent}", incoming ? "in" : "out",
            m.Type, e.ConversationId, e.ReplyTo, e.OperationId, e.Status, e.Subject, e.ContentType, e.Sequence, m.CreatedOn, m.StoredOn, m.ExpiresOn,
            m.State, m.StateDetail, m.Envelope is null, content?.Body, content?.Metadata);
    }

    public static void MapLocalApi(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        app.MapGet("/status", (Daemon d) => Handle(async () =>
        {
            var relay = d.Relay;
            return Results.Ok(new LocalStatus(d.MachineId, relay.Name, WireProtocol.NameOf(relay.Status), relay.IsConnected, relay.IsReachable,
                relay.RouterUrl.ToString(),
                relay.ClockOffset, relay.RouterClockSkew, d.Context.Home, d.Store.GetOutbox(d.MachineId).Count, Agents(d),
                await d.Store.GetMetricsAsync()));
        }));

        app.MapGet("/machines", (Daemon d) => Handle(() =>
        {
            var announced = d.Store.GetPeerAgents().ToLookup(e => e.Machine);
            return Task.FromResult(Results.Ok(d.Relay.Machines.Where(m => m.Id != d.MachineId).OrderBy(m => m.Name).Select(m =>
                new LocalMachine(m.Id, m.Name, m.Online, m.LastSeenOn, m.Description.OperatingSystem, [.. announced[m.Id].Select(e =>
                    new LocalPeerAgent(e.Agent, m.Online ? WireProtocol.NameOf(e.State) : "offline", e.ChangedOn, e.Roles))])).ToList()));
        }));

        app.MapPost("/send", (Daemon d, LocalSendRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(request.From);
            if (request.To.Any(t => t.StartsWith(RoomAddress.Prefix, StringComparison.Ordinal)))
            {
                if (request.To is not [var room] || request.Attachments is { Count: > 0 } || request.ConversationId is not null)
                {
                    throw new ArgumentException("a room is sent to on its own, without files or a conversation id (the room is the conversation)");
                }

                var said = await d.SendToRoomAsync(request.From, room, new OutgoingMessage(request.Body ?? throw new ArgumentException("a body is required"))
                {
                    Subject = request.Subject,
                    ReplyTo = request.ReplyTo,
                    ContentType = request.ContentType ?? "text/plain",
                    Metadata = request.Metadata,
                    ExpiresIn = request.ExpiresIn,
                }, ct);
                return Results.Ok(new List<LocalSent> { new(said.Id, $"room:{d.RequireRoom(room).Name}", said.ConversationId, said.Sequence, said.ExpiresOn,
                    said.State is { } state ? WireProtocol.NameOf(state) : "outbox") });
            }

            var sent = await d.SendAsync(request.To, new OutgoingMessage(request.Body ?? throw new ArgumentException("a body is required"))
            {
                FromAgent = request.From,
                Subject = request.Subject,
                ConversationId = request.ConversationId,
                ReplyTo = request.ReplyTo,
                OperationId = request.OperationId,
                Status = ParseStatus(request.Status),
                ContentType = request.ContentType ?? "text/plain",
                Metadata = request.Metadata,
                ExpiresIn = request.ExpiresIn,
            }, ct, request.Attachments);
            return Results.Ok(sent.Select(d.ToLocal).ToList());
        }));

        app.MapPost("/agents/{agent}/reply", (Daemon d, string agent, LocalReplyRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(agent);
            var sent = await d.ReplyAsync(agent, request.Id, new OutgoingMessage(request.Body ?? throw new ArgumentException("a body is required"))
            {
                Subject = request.Subject,
                Status = ParseStatus(request.Status),
                ContentType = request.ContentType ?? "text/plain",
                Metadata = request.Metadata,
                ExpiresIn = request.ExpiresIn,
            }, ct, request.Attachments);
            return Results.Ok(d.ToLocal(sent));
        }));

        app.MapPost("/agents/{agent}/ack", (Daemon d, string agent, LocalAckRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(agent);
            return Results.Ok(d.ToLocal(await d.AcknowledgeAsync(agent, request.Id, ct), MessageDirection.In));
        }));

        app.MapGet("/agents/{agent}/inbox", (Daemon d, string agent) => Handle(() =>
        {
            RequireAgent(agent);
            var inbox = d.Store.GetInbox(d.MachineId, agent);
            var messages = inbox.Select(m => d.ToLocal(m, MessageDirection.In)).ToList();
            d.MarkFetched(inbox);
            return Task.FromResult(Results.Ok(messages));
        }));

        app.MapGet("/agents/{agent}/messages/{id:guid}", (Daemon d, string agent, Guid id) => Handle(() =>
        {
            RequireAgent(agent);
            var (stored, direction) = d.Store.FindIncoming(d.MachineId, agent, id) is { } incoming ? (incoming, MessageDirection.In)
                : d.Store.FindOutgoing(d.MachineId, agent, id) is { } outgoing ? (outgoing, MessageDirection.Out)
                : throw new KeyNotFoundException($"agent '{agent}' has no message {id}");
            return Task.FromResult(Results.Ok(new LocalMessageDetail(d.ToLocal(stored, direction),
                [.. d.Store.GetEvents(stored.FromMachine, id).Where(e => e.Direction == direction)])));
        }));

        app.MapGet("/agents/{agent}/conversations/{id:guid}", (Daemon d, string agent, Guid id) => Handle(() =>
        {
            RequireAgent(agent);
            return Task.FromResult(Results.Ok(d.Store.GetConversation(d.MachineId, agent, id)
                .Select(m => d.ToLocal(m, m.ToMachine == d.MachineId && m.ToAgent == agent ? MessageDirection.In : MessageDirection.Out)).ToList()));
        }));

        app.MapGet("/agents/{agent}/conversations", (Daemon d, string agent) => Handle(() =>
        {
            RequireAgent(agent);
            return Task.FromResult(Results.Ok(d.Store.GetConversations(agent).Select(c => new LocalConversation(c.Id,
                $"{d.NameOf(c.PeerMachine)}/{c.PeerAgent}", c.Subject, c.StartedBy, c.StartedOn, c.LastActivityOn, c.State)).ToList()));
        }));

        app.MapGet("/log", (Daemon d, Guid? conversation, Guid? operation, string? peer, string? agent, DateTimeOffset? since, int? limit,
            bool? automatic, bool? bodies) => Handle(() =>
        {
            var peerId = peer is null ? null
                : d.Relay.Machines.FirstOrDefault(m => m.Name == peer || m.Id == peer)?.Id ?? throw new KeyNotFoundException($"no machine '{peer}'");
            var entries = d.Store.GetLog(d.MachineId, new LogQuery(conversation, operation, peerId, agent, since, automatic ?? false, limit ?? 1000));
            return Task.FromResult(Results.Ok(entries.Select(e => ToLog(d, e, bodies ?? false)).ToList()));
        }));

        app.MapPost("/agents/{agent}/fetch", (Daemon d, string agent, LocalFileRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(agent);
            return Results.Ok(await d.FetchAsync(agent, request.Id, request.BlobId, ct));
        }));

        app.MapPost("/agents/{agent}/release", (Daemon d, string agent, LocalFileRequest request) => Handle(() =>
        {
            RequireAgent(agent);
            return Task.FromResult(Results.Ok(d.Release(agent, request.Id, request.BlobId)));
        }));

        app.MapGet("/blobs", (Daemon d, CancellationToken ct) => Handle(async () => Results.Ok(await d.Relay.GetBlobsAsync(ct))));

        app.MapPost("/blobs/{id:guid}/cancel", (Daemon d, Guid id, CancellationToken ct) => Handle(async () =>
        {
            await d.CancelAsync(id, ct);
            return Results.Ok(new LocalOk(true));
        }));

        app.MapGet("/rooms", (Daemon d, string? agent) => Results.Ok(d.Rooms(agent)));

        app.MapGet("/rooms/invites", (Daemon d, string? agent) => Results.Ok(d.RoomInvites(agent)));

        app.MapPost("/rooms", (Daemon d, LocalRoomCreate request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(request.Agent);
            return Results.Ok(await d.CreateRoomAsync(request.Agent, request.Name, request.Invite ?? [], ct));
        }));

        app.MapPost("/rooms/{room}/invite", (Daemon d, string room, LocalRoomInviteRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(request.Agent);
            return Results.Ok(await d.InviteToRoomAsync(request.Agent, room, request.Invite ?? [], ct));
        }));

        app.MapPost("/rooms/{room}/join", (Daemon d, string room, LocalRoomAgent request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(request.Agent);
            return Results.Ok(await d.JoinRoomAsync(request.Agent, room, ct));
        }));

        app.MapPost("/rooms/{room}/leave", (Daemon d, string room, LocalRoomAgent request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(request.Agent);
            return Results.Ok(await d.LeaveRoomAsync(request.Agent, room, ct));
        }));

        app.MapPost("/rooms/{room}/send", (Daemon d, string room, LocalRoomSend request, CancellationToken ct) => Handle(async () =>
        {
            RequireAgent(request.Agent);
            var sent = await d.SendToRoomAsync(request.Agent, room, new OutgoingMessage(request.Body ?? throw new ArgumentException("a body is required"))
            {
                ReplyTo = request.ReplyTo,
                Subject = request.Subject,
                ContentType = request.ContentType ?? "text/plain",
                Metadata = request.Metadata,
            }, ct);
            return Results.Ok(new LocalSent(sent.Id, $"room:{d.RequireRoom(room).Name}", sent.ConversationId, sent.Sequence, sent.ExpiresOn,
                sent.State is { } s ? WireProtocol.NameOf(s) : "outbox"));
        }));

        app.MapGet("/rooms/{room}/messages", (Daemon d, string room, string agent, DateTimeOffset? since, int? limit) => Handle(() =>
        {
            RequireAgent(agent);
            return Task.FromResult(Results.Ok(d.ReadRoom(agent, room, since, limit ?? 200)));
        }));

        app.MapGet("/agents", (Daemon d) => Results.Ok(Agents(d)));

        app.MapPost("/agents/{agent}/retire", (Daemon d, string agent) => Handle(async () =>
        {
            RequireAgent(agent);
            if (d.Sessions.IsOnline(agent))
            {
                throw new InvalidOperationException($"agent '{agent}' is online; stop it before retiring it");
            }

            if (!await d.RetireAsync(agent))
            {
                throw new KeyNotFoundException($"agent '{agent}' isn't registered (or is already retired)");
            }

            return Results.Ok(Agents(d).Single(e => e.Name == agent));
        }));

        app.MapGet("/diagnostics", (Daemon d) => Results.Ok(d.Store.GetDiagnostics()));

        // Joining goes through the daemon: in system mode only it holds the keys.
        app.MapPost("/join", (Daemon d, LocalJoinRequest request, CancellationToken ct) => Handle(async () =>
        {
            if (!d.Relay.IsReachable)
            {
                throw new InvalidOperationException("the daemon can't reach the router yet; see 'poniexp-client doctor'");
            }

            var status = d.Relay.Status == MembershipStatus.Approved ? MembershipStatus.Approved
                : await d.Relay.RequestJoinAsync(request.Name, MachineDescription.Detect(request.Notes), ct);
            return Results.Ok(new LocalJoinResult(WireProtocol.NameOf(status), d.Relay.Name, d.MachineId, d.Relay.JoinCode));
        }));

        app.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Accepted();
        });

        app.MapPost("/agents/{agent}/heartbeat", (Daemon d, string agent) =>
        {
            if (d.Sessions.Get(agent) is not { } session)
            {
                return Error(StatusCodes.Status404NotFound, $"agent '{agent}' isn't connected");
            }

            session.Beat(d.Now);
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/agents/{agent}", (Daemon d, string agent) =>
        {
            if (d.Sessions.Get(agent) is not { } session)
            {
                return Error(StatusCodes.Status404NotFound, $"agent '{agent}' isn't connected");
            }

            session.End(leaving: true);
            return Results.NoContent();
        });

        app.MapGet("/agents/{agent}/stream", StreamAsync);
    }

    /// <summary>
    /// An agent's live connection: registers it (online), delivers what is waiting for it, then streams
    /// messages and receipts as server-sent events until it leaves, stops or misses its heartbeat.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, Daemon d, string agent, IOptionsMonitor<DaemonOptions> options)
    {
        IReadOnlyList<string> roles;
        try
        {
            RequireAgent(agent);
            roles = http.Request.Query["roles"].ToString() is { Length: > 0 } text ? text.Split(',') : [];
            if (roles.Count > Limits.MaxRoles || roles.Any(r => !Limits.IsValidAgent(r)))
            {
                throw new ArgumentException($"at most {Limits.MaxRoles} roles, each {Limits.AgentRule}");
            }
        }
        catch (ArgumentException e)
        {
            await Error(StatusCodes.Status400BadRequest, e.Message).ExecuteAsync(http);
            return;
        }

        var session = d.Sessions.TryOpen(agent, d.Now);
        if (session is null)
        {
            await Error(StatusCodes.Status409Conflict, $"agent '{agent}' is already connected (another process holds it)").ExecuteAsync(http);
            return;
        }

        using var done = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, session.Ended);
        try
        {
            await d.SetAgentStateAsync(agent, AgentState.Online, roles);
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await WriteAsync(http, new LocalEvent(LocalEventKinds.Registered), done.Token);
            foreach (var waiting in d.Store.GetInbox(d.MachineId, agent))
            {
                d.Deliver(session, waiting);
            }

            while (!done.IsCancellationRequested)
            {
                using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(done.Token);
                keepAlive.CancelAfter(options.CurrentValue.StreamKeepAlive);
                try
                {
                    if (!await session.Events.WaitToReadAsync(keepAlive.Token))
                    {
                        break;
                    }

                    while (session.Events.TryRead(out var e))
                    {
                        await WriteAsync(http, e, done.Token);
                    }
                }
                catch (OperationCanceledException) when (!done.IsCancellationRequested)
                {
                    await http.Response.WriteAsync(": keep-alive\n\n", done.Token);
                    await http.Response.Body.FlushAsync(done.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // the agent went away mid-write
        }
        finally
        {
            d.Sessions.Close(session);
            await d.SetAgentStateAsync(agent, session.Leaving ? AgentState.Left : AgentState.TimedOut);
        }
    }

    private static async Task WriteAsync(HttpContext http, LocalEvent e, CancellationToken ct)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(e, WireProtocol.Json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private static List<LocalAgentInfo> Agents(Daemon d) =>
        [.. d.Store.GetAgents().Select(e => new LocalAgentInfo(e.Name,
            e.RetiredOn is not null ? "retired" : d.Sessions.IsOnline(e.Name) ? "online" : WireProtocol.NameOf(e.State == AgentState.Online
                ? AgentState.TimedOut : e.State), e.ChangedOn, e.RetiredOn, e.Roles))];

    private static void RequireAgent(string? agent)
    {
        if (!Limits.IsValidAgent(agent))
        {
            throw new ArgumentException($"an agent must be {Limits.AgentRule}");
        }

        if (agent is Address.DaemonAgent or RoomAddress.Agent)
        {
            throw new ArgumentException($"'{agent}' is reserved (the daemon's own agent, or a room's)");
        }
    }

    private static ReplyStatus? ParseStatus(string? text) =>
        text is null ? null
        : Enum.GetValues<ReplyStatus>().Cast<ReplyStatus?>().FirstOrDefault(s => WireProtocol.NameOf(s!.Value) == text)
          ?? throw new ArgumentException("status is progress, done or failed");

    private static async Task<IResult> Handle(Func<Task<IResult>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return Error(StatusCodes.Status400BadRequest, e is ArgumentException { ParamName: { } p } a
                ? a.Message.Replace($" (Parameter '{p}')", string.Empty, StringComparison.Ordinal) : e.Message);
        }
        catch (KeyNotFoundException e)
        {
            return Error(StatusCodes.Status404NotFound, e.Message);
        }
        catch (InvalidOperationException e)
        {
            return Error(StatusCodes.Status409Conflict, e.Message);
        }
        catch (HubException e)
        {
            return Error(StatusCodes.Status502BadGateway, $"the router refused it: {RelayClient.ReasonOf(e)}");
        }
        catch (BlobTransferException e)
        {
            return Error(e.Definitive ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status502BadGateway, $"download failed: {e.Message}");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return Error(StatusCodes.Status400BadRequest, $"can't read the file: {e.Message}");
        }
    }

    private static IResult Error(int status, string message) => Results.Json(new LocalError(message), WireProtocol.Json, statusCode: status);
}
