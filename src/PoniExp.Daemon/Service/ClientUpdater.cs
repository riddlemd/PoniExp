namespace PoniExp.Daemon.Service;

/// <summary>What <see cref="ClientUpdater"/> needs from the platform: the service manager and the binaries. Faked in tests.</summary>
public interface IServiceControl
{
    /// <summary>The version a client binary reports (<c>&lt;exe&gt; version</c>); throws if it doesn't run here.</summary>
    Task<string> VersionOfAsync(string exe, CancellationToken ct);

    /// <summary>Stops the daemon's service and waits until the daemon no longer answers.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Starts the daemon's service; throws if the service manager refuses.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>The version the daemon reports once it answers, or null if it doesn't answer in time.</summary>
    Task<string?> WaitForDaemonAsync(CancellationToken ct);
}

/// <summary>
/// Replaces the client binary a service runs: stop, swap, start, check the daemon answers with the new version; the old
/// binary goes back (and is started) if that fails. The old binary is renamed aside rather than overwritten: Windows
/// can't overwrite a running exe (this process may be it), and on macOS writing over a signed binary in place gets the
/// next process killed.
/// </summary>
public sealed class ClientUpdater(IServiceControl service, TextWriter log)
{
    /// <summary>Installs <paramref name="source"/> as <paramref name="target"/> and returns the version the daemon then reports.</summary>
    public async Task<string> UpdateAsync(string source, string target, CancellationToken ct)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        if (!File.Exists(source))
        {
            throw new ArgumentException($"no client binary at {source} (build one with scripts/publish.sh <rid>)");
        }

        if (!File.Exists(target))
        {
            throw new ArgumentException($"no installed client at {target}");
        }

        if (string.Equals(source, target, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{source} is the installed client itself; give the path of the new build");
        }

        // Whatever can fail without touching the service happens while it still runs: the new binary runs here, and is staged beside the old.
        var expected = await service.VersionOfAsync(source, ct);
        var staged = target + ".new";
        var backup = target + ".old";
        TryDelete(backup); // left by an earlier update on Windows, where the binary that ran it couldn't be deleted
        File.Copy(source, staged, overwrite: true); // keeps the execute bit

        log.WriteLine("stopping the service");
        try
        {
            await service.StopAsync(ct);
        }
        catch
        {
            TryDelete(staged);
            throw;
        }

        File.Move(target, backup);
        File.Move(staged, target);
        try
        {
            log.WriteLine($"starting {expected}");
            await service.StartAsync(ct);
            var running = await service.WaitForDaemonAsync(ct);
            if (running != expected)
            {
                throw new InvalidOperationException(running is null ? "the daemon didn't answer" : $"the daemon reports {running}, not {expected}");
            }
        }
        catch (Exception e)
        {
            throw await RollBackAsync(target, backup, e);
        }

        TryDelete(backup);
        return expected;
    }

    private async Task<Exception> RollBackAsync(string target, string backup, Exception cause)
    {
        log.WriteLine($"update failed ({cause.Message}); putting the previous binary back");
        var failed = target + ".failed";
        try
        {
            // Not cancellable: a half-done rollback is worse than a slow one.
            await service.StopAsync(CancellationToken.None);
            File.Move(target, failed, overwrite: true);
            File.Move(backup, target);
            await service.StartAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            return new InvalidOperationException($"update failed ({cause.Message}) and so did the rollback ({e.Message}); " +
                                                 $"the previous binary is {(File.Exists(backup) ? backup : target)}", cause);
        }

        TryDelete(failed);
        return new InvalidOperationException($"update failed ({cause.Message}); rolled back to the previous binary", cause);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // still running (Windows): the next update removes it
        }
    }
}
