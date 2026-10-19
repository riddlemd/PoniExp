using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>Builds the daemon: its services, settings (settings.json in the home directory) and the local API on its pipe or socket.</summary>
public static class DaemonHost
{
    /// <param name="args">Host arguments (e.g. <c>--Logging:LogLevel:Default=None</c>).</param>
    /// <param name="configure">Extra configuration sources (tests add in-memory settings here).</param>
    /// <param name="windowsService">Running under the Windows service manager (system mode).</param>
    public static WebApplication Build(DaemonContext context, string[]? args = null, Action<IConfigurationBuilder>? configure = null,
        bool windowsService = false)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args ?? [], ContentRootPath = context.Home });
        builder.Configuration
            .AddJsonFile(Path.Combine(context.Home, "settings.json"), optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("PONIEXP_");
        configure?.Invoke(builder.Configuration);
        if (windowsService)
        {
            builder.Services.AddWindowsService(o => o.ServiceName = "PoniExp");
        }

        builder.Services.AddOptions<DaemonOptions>().Bind(builder.Configuration.GetSection(DaemonOptions.Section)).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<DaemonOptions>, DaemonOptionsValidator>();
        builder.Services.AddOptions<MessagingOptions>().Bind(builder.Configuration.GetSection(MessagingOptions.Section)).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<MessagingOptions>, MessagingOptionsValidator>();
        builder.Services.AddOptions<RetentionOptions>().Bind(builder.Configuration.GetSection(RetentionOptions.Section)).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<RetentionOptions>, RetentionOptionsValidator>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton(sp => new RelayClient(context.Keys, context.Relay, context.Store,
            sp.GetRequiredService<IOptionsMonitor<MessagingOptions>>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<AgentSessions>();
        builder.Services.AddSingleton<Daemon>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Daemon>());
        builder.Services.ConfigureHttpJsonOptions(o => WireProtocol.Configure(o.SerializerOptions));

        var ownerSid = builder.Configuration.GetSection(DaemonOptions.Section)[nameof(DaemonOptions.OwnerSid)];
        if (OperatingSystem.IsWindows())
        {
            UseNamedPipes(builder, ownerSid);
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (context.Address.PipeName is { } pipe)
            {
                kestrel.ListenNamedPipe(pipe);
            }
            else
            {
                kestrel.ListenUnixSocket(context.Address.SocketPath!);
            }
        });

        var app = builder.Build();
        app.MapLocalApi();
        return app;
    }

    /// <summary>Starts the daemon; refuses if one is already running for this home.</summary>
    public static async Task StartAsync(WebApplication app, DaemonContext context, CancellationToken cancellationToken = default)
    {
        using (var probe = new LocalApiClient(context.Address))
        {
            if (await probe.IsRunningAsync(cancellationToken))
            {
                throw new InvalidOperationException($"a daemon is already running for {context.Home} ({context.Address})");
            }
        }

        if (context.Address.SocketPath is { } socket)
        {
            File.Delete(socket); // left over from a daemon that didn't stop cleanly
        }

        await app.StartAsync(cancellationToken);
        if (context.Address.SocketPath is { } path && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void UseNamedPipes(WebApplicationBuilder builder, string? ownerSid) =>
        builder.WebHost.UseNamedPipes(o =>
        {
            if (ownerSid is not null)
            {
                // System mode: the service's account owns the pipe; the user it serves may use it too.
                o.CurrentUserOnly = false;
                o.PipeSecurity = OwnerOnly(ownerSid);
            }
        });

    [SupportedOSPlatform("windows")]
    private static PipeSecurity OwnerOnly(string ownerSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ownerSid), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
