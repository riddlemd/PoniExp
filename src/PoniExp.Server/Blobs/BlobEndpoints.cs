using PoniExp.Core.Protocol;

namespace PoniExp.Server.Blobs;

/// <summary>
/// The chunk transfer, plain HTTP(S) on the relay port. Every call carries a ticket from the hub
/// (<c>Authorization: Ticket &lt;ticket&gt;</c>) for one blob and one direction; nothing else is accepted.
/// </summary>
public static class BlobEndpoints
{
    public static void MapBlobs(this WebApplication app)
    {
        var blobs = app.MapGroup(HubMethods.BlobsPath);
        blobs.MapGet("/{id:guid}/status", (Guid id, HttpRequest request, BlobRegistry registry) =>
            Authorized(request, registry, id, null) is null ? Results.Unauthorized() : Run(() => Results.Ok(registry.Status(id))));
        blobs.MapPut("/{id:guid}/chunks/{n:int}", (Guid id, int n, HttpRequest request, BlobRegistry registry, CancellationToken ct) =>
            PutAsync(request, registry, id, n, ct));
        blobs.MapPut("/{id:guid}/manifest", (Guid id, HttpRequest request, BlobRegistry registry, CancellationToken ct) =>
            PutAsync(request, registry, id, null, ct));
        blobs.MapGet("/{id:guid}/chunks/{n:int}", (Guid id, int n, HttpRequest request, BlobRegistry registry) => Get(request, registry, id, n));
        blobs.MapGet("/{id:guid}/manifest", (Guid id, HttpRequest request, BlobRegistry registry) => Get(request, registry, id, null));
    }

    private static async Task<IResult> PutAsync(HttpRequest request, BlobRegistry registry, Guid id, int? n, CancellationToken ct)
    {
        if (Authorized(request, registry, id, TicketDirection.Upload) is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            await registry.PutAsync(id, n, request.Body, ct);
            return Results.NoContent();
        }
        catch (RouterException e)
        {
            return Problem(e);
        }
    }

    private static IResult Get(HttpRequest request, BlobRegistry registry, Guid id, int? n)
    {
        if (Authorized(request, registry, id, TicketDirection.Download) is null)
        {
            return Results.Unauthorized();
        }

        return registry.FileOf(id, n) is { } path ? Results.File(path, "application/octet-stream") : Results.NotFound();
    }

    private static Ticket? Authorized(HttpRequest request, BlobRegistry registry, Guid id, TicketDirection? direction)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Ticket ", StringComparison.Ordinal) ? registry.Check(header["Ticket ".Length..].Trim(), id, direction) : null;
    }

    private static IResult Run(Func<IResult> work)
    {
        try
        {
            return work();
        }
        catch (RouterException e)
        {
            return Problem(e);
        }
    }

    private static IResult Problem(RouterException e) => Results.Problem(e.Message, statusCode: e.Kind switch
    {
        RouterErrorKind.NotFound => StatusCodes.Status404NotFound,
        RouterErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    });
}
