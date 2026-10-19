using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;
using PoniExp.Server.Admin;
using PoniExp.Server.Hubs;
using PoniExp.Server.Machines;
using PoniExp.Server.Security;

namespace PoniExp.Server;

public static class RouterHost
{
    /// <param name="configure">Applied after configuration binding, on every reload (tests use it to pin ports and paths).</param>
    /// <param name="configuration">Extra configuration sources (tests add a reloadable in-memory one).</param>
    public static WebApplication Build(string[] args, Action<RouterOptions>? configure = null,
        Action<IConfigurationBuilder>? configuration = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configuration?.Invoke(builder.Configuration);
        var section = builder.Configuration.GetSection(RouterOptions.Section);

        // The listener, TLS and data directory are fixed at startup; everything else is read live.
        var options = section.Get<RouterOptions>() ?? new RouterOptions();
        configure?.Invoke(options);

        var time = TimeProvider.System;
        var certificate = options.UseHttps ? RouterCertificate.LoadOrCreate(options.DataDirectory, time) : null;
        var certificateInfo = new RouterCertificateInfo(certificate is null ? null : RouterCertificate.Sha256Of(certificate));

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxConcurrentConnections = options.MaxConnections;
            kestrel.Limits.MaxConcurrentUpgradedConnections = options.MaxConnections;
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

        builder.Services.AddOptions<RouterOptions>()
            .Bind(section)
            .PostConfigure(o => configure?.Invoke(o))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<RouterOptions>, RouterOptionsValidator>();
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(certificateInfo);
        // Within docker stop's 10 s grace; daemons reconnect to the next instance anyway.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton(new RouterStore(options.DataDirectory));
        builder.Services.AddSingleton<TrafficLog>();
        builder.Services.AddSingleton<MachineRegistry>();
        builder.Services.AddSingleton<ConnectionGate>();
        builder.Services.AddHostedService<RouterMaintenance>();
        builder.Services.AddSingleton<MessageQueue>();
        builder.Services.AddSingleton<PresenceTracker>();
        builder.Services.AddSingleton<AdminService>();
        builder.Services.AddSignalR(signalR =>
            {
                signalR.MaximumReceiveMessageSize = Limits.MaxContentBytes * 2 + 64 * 1024;
                signalR.EnableDetailedErrors = false; // HubException messages still reach the caller
            })
            .AddJsonProtocol(json => WireProtocol.Configure(json.PayloadSerializerOptions));

        var app = builder.Build();
        app.MapHub<RelayHub>(HubMethods.Path);
        app.MapAdmin();
        app.MapGet("/", () => Results.Text("PoniExp router\n"));
        // Says only whether the router works: no counts or names, since the relay port may face the internet.
        app.MapGet("/health", (RouterStore store) => store.IsHealthy()
            ? Results.Json(new { status = "healthy" })
            : Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable));

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
