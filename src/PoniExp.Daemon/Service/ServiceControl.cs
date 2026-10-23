using System.ComponentModel;
using System.Diagnostics;
using PoniExp.Client;

namespace PoniExp.Daemon.Service;

/// <summary>
/// The real <see cref="IServiceControl"/>: runs a <see cref="ServiceControlPlan"/>'s commands and asks the daemon's local API
/// whether it answers. Tests use a fake: this one stops and starts the machine's actual service.
/// </summary>
public sealed class ServiceControl(ServiceControlPlan plan, LocalAddress address, TextWriter log) : IServiceControl
{
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<string> VersionOfAsync(string exe, CancellationToken ct)
    {
        var (code, output) = await RunAsync([exe, "version"], ct);
        return code == 0 && output.Trim() is { Length: > 0 } version
            ? version
            : throw new InvalidOperationException($"{exe} doesn't run here, or is too old to update with ({code}): {output.Trim()}");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        using var api = new LocalApiClient(address);
        if (plan.StopThroughApi && await api.IsRunningAsync(ct))
        {
            await api.ShutdownAsync(ct);
        }

        foreach (var command in plan.Stop)
        {
            // Fails when the service wasn't running; what counts is that the daemon stops answering.
            await RunAsync(command, ct);
        }

        var until = DateTimeOffset.UtcNow + StopTimeout;
        while (await api.IsRunningAsync(ct))
        {
            if (DateTimeOffset.UtcNow > until)
            {
                throw new InvalidOperationException($"the daemon still answers on {address} after stopping its service; " +
                                                    "is one running outside it ('poniexp-client daemon')?");
            }

            await Task.Delay(500, ct);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var command in plan.Start)
        {
            var (code, output) = await RunAsync(command, ct);
            if (code != 0)
            {
                throw new InvalidOperationException($"'{string.Join(' ', command.Take(2))}' failed ({code}): {output.Trim()}");
            }
        }

        if (plan.StartDetached is { } detached)
        {
            log.WriteLine($"  start {ServiceInstaller.Quote(detached)}");
            // Shell-executed, so it inherits none of this process's handles (a redirected stdout would stay open).
            Process.Start(new ProcessStartInfo(detached[0], detached[1..]) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
        }
    }

    public async Task<string?> WaitForDaemonAsync(CancellationToken ct)
    {
        using var api = new LocalApiClient(address);
        var until = DateTimeOffset.UtcNow + StartTimeout;
        while (!await api.IsRunningAsync(ct))
        {
            if (DateTimeOffset.UtcNow > until)
            {
                return null;
            }

            await Task.Delay(500, ct);
        }

        return (await api.StatusAsync(ct)).Version ?? "unknown (a daemon older than 'update')";
    }

    private async Task<(int Code, string Output)> RunAsync(string[] command, CancellationToken ct)
    {
        log.WriteLine($"  run   {ServiceInstaller.Quote(command)}");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command[0], command[1..])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException($"can't run {command[0]}");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"can't run {command[0]}: {e.Message}", e);
        }
    }
}
