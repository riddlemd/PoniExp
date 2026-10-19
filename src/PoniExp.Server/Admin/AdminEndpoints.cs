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
