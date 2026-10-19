using System.Security;
using PoniExp.Client;

namespace PoniExp.Daemon.Service;

public enum ServiceMode
{
    /// <summary>Starts at logon, as the user: keys in the user's own secret store.</summary>
    User,
    /// <summary>Starts at boot, under its own account (Windows, macOS) or the user's lingering session (Linux).</summary>
    System,
}

/// <summary>A file the plan writes.</summary>
public sealed record PlannedFile(string Path, string Content);

/// <summary>
/// What installing or removing the daemon as a service does on this OS: directories to copy the app into,
/// files to write, commands to run (in order), and a command that starts it now without waiting.
/// Built separately from running it, so it can be shown (<c>--dry-run</c>) and tested.
/// </summary>
public sealed record ServicePlan(
    string Description,
    IReadOnlyList<(string From, string To)> Copies,
    IReadOnlyList<PlannedFile> Files,
    IReadOnlyList<string[]> Commands,
    string[]? StartDetached = null,
    bool NeedsAdmin = false,
    string? Notes = null);

public static class ServiceInstaller
{
    public const string Name = "PoniExp";
    public const string LaunchdLabel = "com.poniexp.daemon";
    public const string SystemdUnit = "poniexp.service";
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    /// <param name="exe">The poniexp-client executable.</param>
    /// <param name="home">The user's PoniExp home (user mode), whose router settings a system install copies.</param>
    /// <param name="ownerSid">Windows system mode: the user the service serves (who may open its pipe).</param>
    /// <param name="userHome">The user's home directory (for LaunchAgents and systemd user units).</param>
    public static ServicePlan Install(ServiceMode mode, string exe, string home, string? ownerSid = null, string? userHome = null, OSPlatformKind? os = null)
    {
        userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appDir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var exeName = Path.GetFileName(exe);
        switch (os ?? Current)
        {
            case OSPlatformKind.Windows when mode == ServiceMode.User:
                var conhost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
                string[] start = [conhost, "--headless", exe, "daemon", "--home", home];
                return new ServicePlan(
                    "Start the daemon at logon, as you (HKCU Run key, no console window)",
                    [], [],
                    [["reg", "add", RunKey, "/v", Name, "/t", "REG_SZ", "/d", Quote(start), "/f"]],
                    start,
                    Notes: "Starts at each logon. It isn't restarted if it stops; 'poniexp-client service status' shows whether it runs.");

            case OSPlatformKind.Windows:
                var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);
                var systemHome = LocalAddress.SystemHome;
                var serviceExe = Path.Combine(programFiles, exeName);
                return new ServicePlan(
                    @"Run the daemon at boot as the Windows service 'PoniExp', under its own account NT SERVICE\PoniExp",
                    [(appDir, programFiles), (Path.Combine(home, "config.json"), Path.Combine(systemHome, "config.json"))],
                    [new PlannedFile(Path.Combine(systemHome, "settings.json"),
                        $$"""{ "Daemon": { "OwnerSid": "{{ownerSid ?? throw new ArgumentException("the owner's SID is needed")}}" } }""" + "\n")],
                    [
                        ["sc.exe", "create", Name, "binPath=", Quote([serviceExe, "daemon", "--windows-service", "--home", systemHome]), "start=", "auto",
                            "obj=", $@"NT SERVICE\{Name}", "DisplayName=", "PoniExp daemon"],
                        ["sc.exe", "description", Name, "PoniExp: this machine's connection to the relay"],
                        ["sc.exe", "failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000"],
                        ["icacls", systemHome, "/inheritance:r", "/grant:r", $@"NT SERVICE\{Name}:(OI)(CI)M", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"],
                        ["sc.exe", "start", Name],
                    ],
                    NeedsAdmin: true,
                    Notes: "The service has its own keys (a new machine identity, kept in its account's DPAPI). Join it with " +
                           "'poniexp-client join <name>', which goes through the service. Only you and the service can open its pipe.");

            case OSPlatformKind.MacOS when mode == ServiceMode.User:
                var agent = Path.Combine(userHome, "Library", "LaunchAgents", LaunchdLabel + ".plist");
                return new ServicePlan(
                    "Start the daemon at login, as you (launchd LaunchAgent)",
                    [], [new PlannedFile(agent, Plist([exe, "daemon", "--home", home], userName: null))],
                    [["launchctl", "bootout", $"gui/{Uid()}/{LaunchdLabel}"], ["launchctl", "bootstrap", $"gui/{Uid()}", agent]]);

            case OSPlatformKind.MacOS:
                var daemonPlist = Path.Combine("/Library/LaunchDaemons", LaunchdLabel + ".plist");
                var libDir = "/usr/local/lib/poniexp";
                return new ServicePlan(
                    "Run the daemon at boot (launchd LaunchDaemon, as root, keys in the System keychain)",
                    [(appDir, libDir), (Path.Combine(home, "config.json"), Path.Combine(LocalAddress.SystemHome, "config.json"))],
                    [new PlannedFile(daemonPlist, Plist([Path.Combine(libDir, exeName), "daemon", "--home", LocalAddress.SystemHome], userName: "root"))],
                    [["launchctl", "bootout", $"system/{LaunchdLabel}"], ["launchctl", "bootstrap", "system", daemonPlist]],
                    NeedsAdmin: true,
                    Notes: "The service has its own keys (a new machine identity). Join it with 'poniexp-client join <name>'.");

            default:
                var unit = Path.Combine(userHome, ".config", "systemd", "user", SystemdUnit);
                List<string[]> commands = [["systemctl", "--user", "daemon-reload"], ["systemctl", "--user", "enable", "--now", SystemdUnit]];
                if (mode == ServiceMode.System)
                {
                    commands.Insert(0, ["loginctl", "enable-linger", Environment.UserName]);
                }

                return new ServicePlan(
                    mode == ServiceMode.User
                        ? "Start the daemon at login, as you (systemd user unit)"
                        : "Start the daemon at boot, as you (systemd user unit, with lingering so it runs while you're logged out)",
                    [], [new PlannedFile(unit, Unit([exe, "daemon", "--home", home]))], commands);
        }
    }

    public static ServicePlan Uninstall(ServiceMode mode, string? userHome = null, OSPlatformKind? os = null)
    {
        userHome ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return (os ?? Current) switch
        {
            OSPlatformKind.Windows when mode == ServiceMode.User => new ServicePlan("Stop starting the daemon at logon",
                [], [], [["reg", "delete", RunKey, "/v", Name, "/f"]]),
            OSPlatformKind.Windows => new ServicePlan("Stop and remove the Windows service 'PoniExp' (its keys and data stay in " +
                LocalAddress.SystemHome + ")", [], [], [["sc.exe", "stop", Name], ["sc.exe", "delete", Name]], NeedsAdmin: true),
            OSPlatformKind.MacOS when mode == ServiceMode.User => new ServicePlan("Remove the LaunchAgent", [], [],
                [["launchctl", "bootout", $"gui/{Uid()}/{LaunchdLabel}"], ["rm", "-f", Path.Combine(userHome, "Library", "LaunchAgents", LaunchdLabel + ".plist")]]),
            OSPlatformKind.MacOS => new ServicePlan("Remove the LaunchDaemon (its keys and data stay)", [], [],
                [["launchctl", "bootout", $"system/{LaunchdLabel}"], ["rm", "-f", Path.Combine("/Library/LaunchDaemons", LaunchdLabel + ".plist")]],
                NeedsAdmin: true),
            _ => new ServicePlan("Remove the systemd user unit", [], [],
                [["systemctl", "--user", "disable", "--now", SystemdUnit], ["rm", "-f", Path.Combine(userHome, ".config", "systemd", "user", SystemdUnit)],
                    .. mode == ServiceMode.System ? new[] { new[] { "loginctl", "disable-linger", Environment.UserName } } : []]),
        };
    }

    public static OSPlatformKind Current =>
        OperatingSystem.IsWindows() ? OSPlatformKind.Windows : OperatingSystem.IsMacOS() ? OSPlatformKind.MacOS : OSPlatformKind.Linux;

    /// <summary>Joins arguments into one Windows command line, quoting as CommandLineToArgvW expects.</summary>
    public static string Quote(IEnumerable<string> args) => string.Join(' ', args.Select(QuoteOne));

    private static string QuoteOne(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return arg;
        }

        var quoted = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static string Plist(IReadOnlyList<string> args, string? userName) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{LaunchdLabel}</string>
          <key>ProgramArguments</key>
          <array>
        {string.Join("\n", args.Select(a => $"    <string>{SecurityElement.Escape(a)}</string>"))}
          </array>
          <key>RunAtLoad</key><true/>
          <key>KeepAlive</key><true/>{(userName is null ? string.Empty : $"\n  <key>UserName</key><string>{userName}</string>")}
        </dict>
        </plist>

        """;

    private static string Unit(IReadOnlyList<string> args) => $"""
        [Unit]
        Description=PoniExp daemon (this machine's connection to the relay)
        After=network-online.target

        [Service]
        ExecStart={string.Join(' ', args.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a))}
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=default.target

        """;

    private static string Uid() => OperatingSystem.IsWindows() ? "0" : Environment.GetEnvironmentVariable("UID") ?? GetUid().ToString(System.Globalization.CultureInfo.InvariantCulture);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}

public enum OSPlatformKind
{
    Windows,
    MacOS,
    Linux,
}
