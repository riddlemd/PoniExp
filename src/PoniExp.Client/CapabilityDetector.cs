using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PoniExp.Core.Protocol;

namespace PoniExp.Client;

/// <summary>
/// Detects what this machine has, for job requirements: OS, architecture, CPU, cores, memory, and graphics cards (from
/// nvidia-smi where it's installed, else what the OS lists). Tags come from the daemon's settings. Memory is what the
/// runtime may use: the machine's RAM, or a container's limit.
/// </summary>
public static class CapabilityDetector
{
    public static MachineCapabilities Detect(IReadOnlyList<string> tags, DateTimeOffset now)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";
        var memory = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024));
        return new MachineCapabilities(os, Environment.OSVersion.Version.ToString(), RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Quietly(Cpu), Environment.ProcessorCount, memory, Quietly(Gpus) ?? [], [.. tags.Distinct()], now);
    }

    private static string? Cpu()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsCpu();
        }

        if (OperatingSystem.IsMacOS())
        {
            return Run("sysctl", "-n machdep.cpu.brand_string")?.Trim();
        }

        return File.Exists("/proc/cpuinfo")
            ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim()
            : null;
    }

    private static IReadOnlyList<GpuInfo> Gpus()
    {
        // NVIDIA's own tool, where installed, knows names and memory exactly.
        if (Run("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits") is { } smi)
        {
            var cards = smi.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(',', 2, StringSplitOptions.TrimEntries))
                .Where(p => p.Length == 2)
                .Select(p => new GpuInfo(p[0], double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ? Math.Round(mb / 1024) : null))
                .ToList();
            if (cards.Count > 0)
            {
                return cards;
            }
        }

        return OperatingSystem.IsWindows() ? WindowsGpus() : [];
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsCpu()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim();
    }

    /// <summary>Display adapters from the registry (the device class for displays), skipping Microsoft's basic ones.</summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<GpuInfo> WindowsGpus()
    {
        using var adapters = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        var cards = new List<GpuInfo>();
        foreach (var name in adapters?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
        {
            try
            {
                using var adapter = adapters!.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is not string description || description.StartsWith("Microsoft", StringComparison.Ordinal))
                {
                    continue;
                }

                var bytes = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q => q,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b),
                    _ => adapter.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int d => (uint)d,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b),
                        _ => 0L,
                    },
                };
                cards.Add(new GpuInfo(description, bytes > 0 ? Math.Round(bytes / (1024.0 * 1024 * 1024)) : null));
            }
            catch (System.Security.SecurityException)
            {
                // some adapter keys aren't readable without admin
            }
        }

        return cards;
    }

    private static string? Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // not installed
        }
    }

    private static T? Quietly<T>(Func<T?> detect) where T : class
    {
        try
        {
            return detect();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException)
        {
            return null;
        }
    }
}
