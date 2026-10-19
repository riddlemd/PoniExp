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
/// Each endpoint is shown only its own messages: scoping for convenience, not a security boundary.
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

        return new LocalLogEntry(m.Id, $"{d.NameOf(m.FromMachine)}/{m.FromEndpoint}", $"{d.NameOf(m.ToMachine)}/{m.ToEndpoint}", incoming ? "in" : "out",
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
                relay.ClockOffset, relay.RouterClockSkew, d.Context.Home, d.Store.GetOutbox(d.MachineId).Count, Endpoints(d),
                await d.Store.GetMetricsAsync()));
        }));

        app.MapGet("/machines", (Daemon d) => Handle(() =>
        {
            var announced = d.Store.GetPeerEndpoints().ToLookup(e => e.Machine);
            return Task.FromResult(Results.Ok(d.Relay.Machines.Where(m => m.Id != d.MachineId).OrderBy(m => m.Name).Select(m =>
                new LocalMachine(m.Id, m.Name, m.Online, m.LastSeenOn, m.Description.OperatingSystem, [.. announced[m.Id].Select(e =>
                    new LocalPeerEndpoint(e.Endpoint, m.Online ? WireProtocol.NameOf(e.State) : "offline", e.ChangedOn))])).ToList()));
        }));

        app.MapPost("/send", (Daemon d, LocalSendRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireEndpoint(request.From);
            var sent = await d.SendAsync(request.To, new OutgoingMessage(request.Body ?? throw new ArgumentException("a body is required"))
            {
                FromEndpoint = request.From,
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

        app.MapPost("/endpoints/{endpoint}/reply", (Daemon d, string endpoint, LocalReplyRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireEndpoint(endpoint);
            var sent = await d.ReplyAsync(endpoint, request.Id, new OutgoingMessage(request.Body ?? throw new ArgumentException("a body is required"))
            {
                Subject = request.Subject,
                Status = ParseStatus(request.Status),
                ContentType = request.ContentType ?? "text/plain",
                Metadata = request.Metadata,
                ExpiresIn = request.ExpiresIn,
            }, ct, request.Attachments);
            return Results.Ok(d.ToLocal(sent));
        }));

        app.MapPost("/endpoints/{endpoint}/ack", (Daemon d, string endpoint, LocalAckRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireEndpoint(endpoint);
            return Results.Ok(d.ToLocal(await d.AcknowledgeAsync(endpoint, request.Id, ct), MessageDirection.In));
        }));

        app.MapGet("/endpoints/{endpoint}/inbox", (Daemon d, string endpoint) => Handle(() =>
        {
            RequireEndpoint(endpoint);
            var inbox = d.Store.GetInbox(d.MachineId, endpoint);
            var messages = inbox.Select(m => d.ToLocal(m, MessageDirection.In)).ToList();
            d.MarkFetched(inbox);
            return Task.FromResult(Results.Ok(messages));
        }));

        app.MapGet("/endpoints/{endpoint}/messages/{id:guid}", (Daemon d, string endpoint, Guid id) => Handle(() =>
        {
            RequireEndpoint(endpoint);
            var (stored, direction) = d.Store.FindIncoming(d.MachineId, endpoint, id) is { } incoming ? (incoming, MessageDirection.In)
                : d.Store.FindOutgoing(d.MachineId, endpoint, id) is { } outgoing ? (outgoing, MessageDirection.Out)
                : throw new KeyNotFoundException($"endpoint '{endpoint}' has no message {id}");
            return Task.FromResult(Results.Ok(new LocalMessageDetail(d.ToLocal(stored, direction),
                [.. d.Store.GetEvents(stored.FromMachine, id).Where(e => e.Direction == direction)])));
        }));

        app.MapGet("/endpoints/{endpoint}/conversations/{id:guid}", (Daemon d, string endpoint, Guid id) => Handle(() =>
        {
            RequireEndpoint(endpoint);
            return Task.FromResult(Results.Ok(d.Store.GetConversation(d.MachineId, endpoint, id)
                .Select(m => d.ToLocal(m, m.ToMachine == d.MachineId && m.ToEndpoint == endpoint ? MessageDirection.In : MessageDirection.Out)).ToList()));
        }));

        app.MapGet("/endpoints/{endpoint}/conversations", (Daemon d, string endpoint) => Handle(() =>
        {
            RequireEndpoint(endpoint);
            return Task.FromResult(Results.Ok(d.Store.GetConversations(endpoint).Select(c => new LocalConversation(c.Id,
                $"{d.NameOf(c.PeerMachine)}/{c.PeerEndpoint}", c.Subject, c.StartedBy, c.StartedOn, c.LastActivityOn, c.State)).ToList()));
        }));

        app.MapGet("/log", (Daemon d, Guid? conversation, Guid? operation, string? peer, string? endpoint, DateTimeOffset? since, int? limit,
            bool? automatic, bool? bodies) => Handle(() =>
        {
            var peerId = peer is null ? null
                : d.Relay.Machines.FirstOrDefault(m => m.Name == peer || m.Id == peer)?.Id ?? throw new KeyNotFoundException($"no machine '{peer}'");
            var entries = d.Store.GetLog(d.MachineId, new LogQuery(conversation, operation, peerId, endpoint, since, automatic ?? false, limit ?? 1000));
            return Task.FromResult(Results.Ok(entries.Select(e => ToLog(d, e, bodies ?? false)).ToList()));
        }));

        app.MapPost("/endpoints/{endpoint}/fetch", (Daemon d, string endpoint, LocalFileRequest request, CancellationToken ct) => Handle(async () =>
        {
            RequireEndpoint(endpoint);
            return Results.Ok(await d.FetchAsync(endpoint, request.Id, request.BlobId, ct));
        }));

        app.MapPost("/endpoints/{endpoint}/release", (Daemon d, string endpoint, LocalFileRequest request) => Handle(() =>
        {
            RequireEndpoint(endpoint);
            return Task.FromResult(Results.Ok(d.Release(endpoint, request.Id, request.BlobId)));
        }));

        app.MapGet("/blobs", (Daemon d, CancellationToken ct) => Handle(async () => Results.Ok(await d.Relay.GetBlobsAsync(ct))));

        app.MapPost("/blobs/{id:guid}/cancel", (Daemon d, Guid id, CancellationToken ct) => Handle(async () =>
        {
            await d.CancelAsync(id, ct);
            return Results.Ok(new LocalOk(true));
        }));

        app.MapGet("/endpoints", (Daemon d) => Results.Ok(Endpoints(d)));

        app.MapPost("/endpoints/{endpoint}/retire", (Daemon d, string endpoint) => Handle(async () =>
        {
            RequireEndpoint(endpoint);
            if (d.Sessions.IsOnline(endpoint))
            {
                throw new InvalidOperationException($"endpoint '{endpoint}' is online; stop it before retiring it");
            }

            if (!await d.RetireAsync(endpoint))
            {
                throw new KeyNotFoundException($"endpoint '{endpoint}' isn't registered (or is already retired)");
            }

            return Results.Ok(Endpoints(d).Single(e => e.Name == endpoint));
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

        app.MapPost("/endpoints/{endpoint}/heartbeat", (Daemon d, string endpoint) =>
        {
            if (d.Sessions.Get(endpoint) is not { } session)
            {
                return Error(StatusCodes.Status404NotFound, $"endpoint '{endpoint}' isn't connected");
            }

            session.Beat(d.Now);
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/endpoints/{endpoint}", (Daemon d, string endpoint) =>
        {
            if (d.Sessions.Get(endpoint) is not { } session)
            {
                return Error(StatusCodes.Status404NotFound, $"endpoint '{endpoint}' isn't connected");
            }

            session.End(leaving: true);
            return Results.NoContent();
        });

        app.MapGet("/endpoints/{endpoint}/stream", StreamAsync);
    }

    /// <summary>
    /// An endpoint's live connection: registers it (online), delivers what is waiting for it, then streams
    /// messages and receipts as server-sent events until it leaves, stops or misses its heartbeat.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, Daemon d, string endpoint, IOptionsMonitor<DaemonOptions> options)
    {
        try
        {
            RequireEndpoint(endpoint);
        }
        catch (ArgumentException e)
        {
            await Error(StatusCodes.Status400BadRequest, e.Message).ExecuteAsync(http);
            return;
        }

        var session = d.Sessions.TryOpen(endpoint, d.Now);
        if (session is null)
        {
            await Error(StatusCodes.Status409Conflict, $"endpoint '{endpoint}' is already connected (another process holds it)").ExecuteAsync(http);
            return;
        }

        using var done = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, session.Ended);
        try
        {
            await d.SetEndpointStateAsync(endpoint, EndpointState.Online);
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await WriteAsync(http, new LocalEvent(LocalEventKinds.Registered), done.Token);
            foreach (var waiting in d.Store.GetInbox(d.MachineId, endpoint))
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
            // the endpoint went away mid-write
        }
        finally
        {
            d.Sessions.Close(session);
            await d.SetEndpointStateAsync(endpoint, session.Leaving ? EndpointState.Left : EndpointState.TimedOut);
        }
    }

    private static async Task WriteAsync(HttpContext http, LocalEvent e, CancellationToken ct)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(e, WireProtocol.Json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private static List<LocalEndpointInfo> Endpoints(Daemon d) =>
        [.. d.Store.GetEndpoints().Select(e => new LocalEndpointInfo(e.Name,
            e.RetiredOn is not null ? "retired" : d.Sessions.IsOnline(e.Name) ? "online" : WireProtocol.NameOf(e.State == EndpointState.Online
                ? EndpointState.TimedOut : e.State), e.ChangedOn, e.RetiredOn))];

    private static void RequireEndpoint(string? endpoint)
    {
        if (!Limits.IsValidEndpoint(endpoint))
        {
            throw new ArgumentException($"an endpoint must be {Limits.EndpointRule}");
        }

        if (endpoint == Address.DaemonEndpoint)
        {
            throw new ArgumentException($"'{Address.DaemonEndpoint}' is the daemon's own endpoint");
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
