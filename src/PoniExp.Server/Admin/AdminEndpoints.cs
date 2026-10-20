using PoniExp.Core.Protocol;
using PoniExp.Server.Machines;
using System.Net;
using PoniExp.Server.Security;

namespace PoniExp.Server.Admin;

/// <summary>
/// The admin API. It answers only connections from the loopback address, which inside the container
/// means `docker exec` (published ports arrive from the bridge, not loopback).
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter(async (context, next) =>
            context.HttpContext.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
                ? await next(context)
                : Results.NotFound());

        admin.MapGet("/machines", (AdminService service) => Results.Ok(service.List()));
        admin.MapPost("/machines/{selector}/approve", (string selector, AdminService service) => RunAsync(() => service.ApproveAsync(selector)));
        admin.MapPost("/machines/{selector}/reject", (string selector, AdminService service) => RunAsync(() => service.RejectAsync(selector)));
        admin.MapPost("/machines/{selector}/remove", (string selector, AdminService service) => RunAsync(() => service.RemoveAsync(selector)));
        admin.MapGet("/certificate", (RouterCertificateInfo certificate) => Results.Ok(certificate));
        admin.MapGet("/traffic-log/status", (TrafficLog log) => Results.Ok(new TrafficLogStatus(log.Enabled)));
        admin.MapPost("/traffic-log/{state:regex(^(on|off)$)}", (string state, TrafficLog log) =>
        {
            log.SetEnabled(state == "on");
            return Results.Ok(new TrafficLogStatus(log.Enabled));
        });
        admin.MapGet("/traffic-log", (TrafficLog log, MachineRegistry registry, DateTimeOffset? since, string? machine, int? limit) =>
        {
            var id = machine is null ? null : registry.List().FirstOrDefault(m => m.Name == machine || m.Id == machine)?.Id ?? machine;
            var names = registry.List().ToDictionary(m => m.Id, m => m.Name);
            return Results.Ok(log.Read(since, id, limit ?? 1000).Select(e => new AdminTrafficEntry(e.OccurredOn, e.Event,
                e.FromMachine is { } f ? names.GetValueOrDefault(f, f[..Math.Min(8, f.Length)]) : null,
                e.ToMachine is { } t ? names.GetValueOrDefault(t, t[..Math.Min(8, t.Length)]) : null,
                e.MessageId, e.Size, e.Sequence, e.Address, e.Detail)).ToList());
        });
        admin.MapGet("/groups", (AdminService service) => Results.Ok(service.Groups()));
        admin.MapGet("/rooms", (AdminService service) => Results.Ok(service.Rooms()));
        admin.MapGet("/jobs", (AdminService service, bool? all) => Results.Ok(service.Jobs(all == true)));
        admin.MapGet("/rooms/collapsed", (AdminService service, DateTimeOffset? since) => Results.Ok(service.CollapsedRooms(since)));
        admin.MapPut("/groups/{name}", (string name, GroupInfo body, AdminService service) =>
        {
            try
            {
                return Results.Ok(service.SetGroup(name, body.Members ?? []));
            }
            catch (RouterException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        admin.MapGet("/blobs", (AdminService service) => Results.Ok(service.Blobs()));
        admin.MapPost("/blobs/clear/{selector}", (string selector, AdminService service) =>
        {
            try
            {
                return Results.Ok(new { released = service.ClearBlobsFor(selector) });
            }
            catch (RouterException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound);
            }
        });
        admin.MapPost("/clear-pending", async (AdminService service) => Results.Ok(await service.ClearPendingAsync()));
        admin.MapGet("/backup", (AdminService service, RouterCertificateInfo certificate) => Results.Ok(service.Backup(certificate)));
        admin.MapPost("/restore", async (RouterBackup backup, AdminService service, RouterCertificateInfo certificate) =>
        {
            try
            {
                return Results.Ok(await service.RestoreAsync(backup, certificate));
            }
            catch (Exception e) when (e is RouterException or FormatException or System.Security.Cryptography.CryptographicException)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }

    private static async Task<IResult> RunAsync(Func<Task<AdminMachineView>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (RouterException e)
        {
            var status = e.Kind switch
            {
                RouterErrorKind.NotFound => StatusCodes.Status404NotFound,
                RouterErrorKind.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Problem(e.Message, statusCode: status);
        }
    }
}

public sealed record TrafficLogStatus(bool Enabled);

/// <summary>A traffic log row with machine names (removed machines show their short id).</summary>
public sealed record AdminTrafficEntry(DateTimeOffset OccurredOn, string Event, string? From, string? To, Guid? MessageId, int? Size, long? Sequence,
    string? Address, string? Detail);
