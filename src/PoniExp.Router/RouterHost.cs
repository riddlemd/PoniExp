using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;
using PoniExp.Router.Admin;
using PoniExp.Router.Hubs;
using PoniExp.Router.Machines;
using PoniExp.Router.Security;

namespace PoniExp.Router;

public static class RouterHost
{
    public static WebApplication Build(string[] args, Action<RouterOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = builder.Configuration.GetSection(RouterOptions.Section).Get<RouterOptions>() ?? new RouterOptions();
        configure?.Invoke(options);

        var time = TimeProvider.System;
        var certificate = options.UseHttps ? RouterCertificate.LoadOrCreate(options.DataDirectory, time) : null;
        var certificateInfo = new RouterCertificateInfo(certificate is null ? null : RouterCertificate.Sha256Of(certificate));

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Parse(options.BindAddress), options.Port, listen =>
            {
                if (certificate is not null)
                {
                    listen.UseHttps(certificate);
                }
            });
            if (options.AdminPort is { } adminPort)
            {
                kestrel.Listen(IPAddress.Loopback, adminPort);
            }
        });

        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(certificateInfo);
        builder.Services.AddSingleton<MachineRegistry>();
        builder.Services.AddSingleton<MessageQueue>();
        builder.Services.AddSingleton<PresenceTracker>();
        builder.Services.AddSingleton<AdminService>();
        builder.Services.AddSignalR(signalR =>
        {
            signalR.MaximumReceiveMessageSize = Limits.MaxBodyBytes + 64 * 1024;
            signalR.EnableDetailedErrors = false; // HubException messages still reach the caller
        });

        var app = builder.Build();
        app.MapHub<RelayHub>(HubMethods.Path);
        app.MapAdmin();
        app.MapGet("/", () => Results.Text("PoniExp router\n"));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var logger = app.Services.GetRequiredService<ILogger<RelayHub>>();
            logger.LogInformation("Relay on port {Port} ({Scheme}) at {Path}; certificate SHA-256: {Sha256}",
                options.Port, certificate is null ? "http" : "https", HubMethods.Path, certificateInfo.Sha256 ?? "(no TLS)");
        });
        return app;
    }

    public static IReadOnlyList<string> Addresses(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];
}
