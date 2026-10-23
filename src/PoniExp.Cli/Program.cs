using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Daemon;
using PoniExp.Daemon.Service;
using PoniExp.Secrets;

const string Usage = """
    poniexp-client [--home <dir>] <command>

    Setting up
      init <router-url> [--pin <sha256>]   where the router is; creates this machine's keys
      id                                   this machine's id and join code
      daemon                               run the daemon (the machine's connection) in the foreground
      service install|uninstall [--system] [--dry-run]
                                           start the daemon automatically: at logon (default) or at boot
      service status
      update <new-binary> [--system]       replace the binary the service runs: stop it, swap, start, check the daemon answers
                                           with the new version; the old binary goes back if it doesn't
      version                              this build's version (<version>+<commit>)
      join <name> [--notes <text>]         ask to join; waits for approval (through the daemon if it runs)
      doctor                               check the router, certificate pin, clock, membership and daemon

    Messaging (through the daemon)
      status                               this machine, its agents, and message metrics
      machines                             the other machines and their agents
      send --to <machine[/agent] | group:<g> | room:<name>> [--to ...] [options] <body|->
                                           one separate message per --to
          --from <agent>        sending agent (default: "default")
          --subject <text>         a title for conversation lists
          --conversation <id>      continue a conversation (one --to only)
          --operation <id>         the same id across retries of one action (one --to only)
          --expires <30s|10m|2h|3d>         how long it lives (default and maximum from settings)
          --content-type <type>    how to read the body (default: text/plain)
          --meta <key=value>       metadata; repeatable
          --attach <path>          a file, uploaded once for all recipients; repeatable
      inbox [--agent <name>] [--json]   unacknowledged messages for an agent (default: "default")
      ack <id> [--agent <name>]         acknowledge a message (tell its sender you've taken it on)
      reply <id> [--agent <name>] [--status progress|done|failed] [--meta k=v] [--attach <path>] <body|->
                                           reply in its conversation (acknowledges it too)
      message <id> [--agent <name>]     one message and its delivery history
      conversation <id> [--agent <name>] [--json]
      fetch <id> [--agent <name>] [--blob <id>]
                                           download a message's files (verified) and print where they are
      release <id> [--agent <name>] [--blob <id>]
                                           done with them: the copy goes once every agent has released it
      blobs | cancel <blob-id>             files this machine uploaded; cancel one (the router deletes it)
      listen [--agent <name>] [--json] [--ack] [--auto-fetch]
                                           be the agent: print messages and receipts as they arrive
      conversations [--agent <name>]    an agent's conversations, most recent first
      log [filters] [--json | --export <path|->] [--bodies]
                                           the machine's message log, oldest first
          --conversation <id> --operation <id> --peer <machine> --agent <name>
          --since <2h|3d|date> --limit <n> --automatic (receipts, announcements too)
          --bodies                 decrypt bodies into the output (they're then in the clear)
      job list [--all] [--column <c>]        the job board (open to every approved machine)
      job show <id>
      job post <title> --as <agent> [--description <text|->] [--require <key op value>]... [--backlog]
                                           e.g. --require os=linux --require memory>=4 --require memory<=8 --require gpu~rtx
      capabilities                         what this machine has (job requirements are checked against it)
      job claim|release|cancel <id> --as <agent>
      job move <id> <backlog|ready|running|done|failed> --as <agent> [--note <result>]
      job comment <id> --as <agent> <text>
      room list [--agent <name>]          rooms an agent here is in, was in, or is invited to
      room create <name> --as <agent> [--invite <address>]...
      room invite <room> --as <agent> <address>...   (groups are resolved now)
      room invites [--agent <name>]       open invitations
      room join|leave <room> --as <agent>
      room read <room> --as <agent> [--since <time>] [--json]
      room send <room> --as <agent> [--reply <id>] <body|->   (or: send --to room:<name>)
                                           rooms: named conversations everyone in sees; invitees accept, members leave
      admin <command> [args]               a router admin command from here, signed with your owner key (one prompt):
                                           any `poniexp admin` command (list, approve <code>, group set ..., backup <file>, ...)
      wake                                 wake targets: agents that only use MCP (e.g. Hermes) get a nudge when messages arrive
      wake set <agent> --a2a <url> [--token]
                                           nudge an A2A gateway (Hermes); --token asks for its peer token (kept in the secret store)
      wake set <agent> --command <program> [args...]
                                           run a program (the nudge on stdin; PONIEXP_AGENT, PONIEXP_COUNT, PONIEXP_FROM)
      wake test <agent> | wake remove <agent>
      keys                                 this machine's encryption key: issued, next rotation
      keys rotate                          a new encryption key now (also automatic: Daemon:EncryptionKeyLifetime, 90 days)
      keys reset                           new keys for a revoked machine (daemon stopped): the old data is kept aside; then join again
      owner                                the owner key (router's and pinned here) and the approval desk
      owner pin                            pin the router's owner key here (shows its fingerprint to compare)
      here [--hours <n>]                   make this machine your approval desk (your SSH agent signs: one prompt)
      away                                 end the desk (no prompt from the desk machine itself)
      approvals [--mine]                   what waits on this desk (--mine: what agents here asked)
      approve <id> [--always]              approve (--always: that agent may do it again without asking)
      deny <id> [--reason <text>]
      approvals rules | approvals rules remove <id>
                                           the "always allow" rules on this machine
      agent list | agent retire <name>
                                           the agent registry; a retired agent's messages are rejected
      diagnostics                          recent connection, signature and delivery problems
      mcp --agent <name>                   an MCP server (stdio) for an agent, as that agent
      completion bash|zsh|powershell       tab completion for your shell (ids, agents, rooms...): load it from your profile

    "-" as a body reads stdin. Settings: <home>/settings.json, e.g.
      { "Messaging": { "DefaultExpiry": "1.00:00:00" }, "Daemon": { "AgentTimeout": "00:01:00" } }
    """;

args = TakeHome(args);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return args switch
    {
        ["init", var url, .. var rest] => Init(url, Option(rest, "--pin")),
        ["id"] => Id(),
        ["daemon", .. var rest] => await DaemonAsync(rest.Contains("--windows-service")),
        ["join", var name, .. var rest] => await JoinAsync(name, Option(rest, "--notes")),
        ["doctor"] => await DoctorAsync(),
        ["status"] => await StatusAsync(),
        ["machines"] => await MachinesAsync(),
        ["send", .. var rest] => await SendAsync(rest),
        ["inbox", .. var rest] => await InboxAsync(AgentOf(rest), rest.Contains("--json")),
        ["ack", var id, .. var rest] => await AckAsync(AgentOf(rest), Guid.Parse(id)),
        ["reply", var id, .. var rest] => await ReplyAsync(Guid.Parse(id), rest),
        ["message", var id, .. var rest] => await MessageAsync(AgentOf(rest), Guid.Parse(id)),
        ["conversation", var id, .. var rest] => await ConversationAsync(AgentOf(rest), Guid.Parse(id), rest.Contains("--json")),
        ["listen", .. var rest] => await ListenAsync(AgentOf(rest), rest.Contains("--json"), rest.Contains("--ack"), rest.Contains("--auto-fetch")),
        ["fetch", var id, .. var rest] => await FetchAsync(AgentOf(rest), Guid.Parse(id), Option(rest, "--blob") is { } b ? Guid.Parse(b) : null),
        ["release", var id, .. var rest] => await ReleaseAsync(AgentOf(rest), Guid.Parse(id), Option(rest, "--blob") is { } b ? Guid.Parse(b) : null),
        ["blobs"] => await BlobsAsync(),
        ["room", .. var rest] => await RoomAsync(rest),
        ["job", .. var rest] => await JobAsync(rest),
        ["cancel", var blob] => await CancelBlobAsync(Guid.Parse(blob)),
        ["conversations", .. var rest] => await ConversationsAsync(AgentOf(rest)),
        ["log", .. var rest] => await LogAsync(rest),
        ["agent", "list"] => await AgentsAsync(),
        ["agent", "retire", var name] => await RetireAsync(name),
        ["diagnostics"] => await DiagnosticsAsync(),
        ["mcp", .. var rest] => await McpAsync(Option(rest, "--agent") ?? throw new ArgumentException("mcp needs --agent <name>")),
        ["capabilities"] => await CapabilitiesAsync(),
        ["admin", .. var adminArgs] when adminArgs.Length > 0 => await RemoteAdminAsync(adminArgs),
        ["wake"] => await WakeListAsync(),
        ["wake", "set", var wakeAgent, .. var wakeRest] => await WakeSetAsync(wakeAgent, wakeRest),
        ["wake", "test", var wakeAgent] => await WakeTestAsync(wakeAgent),
        ["wake", "remove", var wakeAgent] => await WakeRemoveAsync(wakeAgent),
        ["keys"] => await KeysAsync(false),
        ["keys", "rotate"] => await KeysAsync(true),
        ["keys", "reset"] => await KeysResetAsync(),
        ["owner"] => await OwnerAsync(),
        ["owner", "pin"] => await PinOwnerAsync(),
        ["here", .. var rest] => await HereAsync(Option(rest, "--hours") is { } h ? TimeSpan.FromHours(double.Parse(h, CultureInfo.InvariantCulture)) : null),
        ["away"] => await AwayAsync(),
        ["approvals", "rules"] => await RulesAsync(),
        ["approvals", "rules", "remove", var rule] => await RemoveRuleAsync(rule),
        ["approvals", .. var rest] => await ApprovalsAsync(rest.Contains("--mine")),
        ["approve", var id, .. var rest] => await AnswerAsync(id, true, rest.Contains("--always"), null),
        ["deny", var id, .. var rest] => await AnswerAsync(id, false, false, Option(rest, "--reason")),
        ["service", "install", .. var rest] => await ServiceInstallAsync(rest.Contains("--system") ? ServiceMode.System : ServiceMode.User, rest.Contains("--dry-run")),
        ["service", "uninstall", .. var rest] => await ServiceUninstallAsync(rest.Contains("--system") ? ServiceMode.System : ServiceMode.User, rest.Contains("--dry-run")),
        ["service", "status"] => await ServiceStatusAsync(),
        ["update", var newBinary, .. var rest] => await UpdateAsync(newBinary, rest.Contains("--system") ? ServiceMode.System : ServiceMode.User),
        ["version" or "--version"] => Version(),
        ["completion", var shell] => Completion(shell),
        ["complete", .. var rest] => await CompleteAsync(rest),
        _ => Fail(Usage),
    };
}
catch (OperationCanceledException)
{
    return 130;
}
catch (OptionsValidationException e)
{
    return Fail($"error: bad settings in {ClientConfig.SettingsPath}: {string.Join(" ", e.Failures)}");
}
catch (Exception e) when (e is PoniExp.Core.Crypto.OwnerKeyUnavailableException or InvalidOperationException or ArgumentException or FormatException or HttpRequestException or LocalApiException
                               or KeyNotFoundException or Microsoft.AspNetCore.SignalR.HubException or System.Net.WebSockets.WebSocketException
                               or IOException)
{
    var message = e.InnerException?.InnerException is System.Security.Authentication.AuthenticationException
        ? "the router's TLS certificate doesn't match the pinned SHA-256 (check 'poniexp-client init ... --pin')"
        : e is ArgumentException { ParamName: { } param } argument
            ? argument.Message.Replace($" (Parameter '{param}')", string.Empty, StringComparison.Ordinal)
            : e.Message;
    return Fail("error: " + message);
}

// ---- setting up ----

int Init(string url, string? pin)
{
    new ClientConfig(url, pin?.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant()).Save();
    Console.WriteLine($"router: {url}{(pin is null ? " (no pin: normal TLS validation)" : $" (pinned {pin[..16]}...)")}");
    return Id();
}

int Id()
{
    using var keys = KeyRing.LoadOrCreate(ClientConfig.HomeDirectory); // creates the home directory and, first time, both keys
    Console.WriteLine($"machine id: {keys.MachineId}");
    Console.WriteLine($"join code:  {KeyFingerprint.JoinCodeOf(keys.PublicKey)}");
    Console.WriteLine($"keys:       {SecretStores.ForHome(ClientConfig.HomeDirectory).Description}");
    return 0;
}

async Task<int> DaemonAsync(bool windowsService)
{
    var config = ClientConfig.Load() ?? throw new InvalidOperationException($"run 'poniexp-client init <router-url>' first (home: {ClientConfig.HomeDirectory})");
    var home = ClientConfig.HomeDirectory;
    if (!OperatingSystem.IsWindows())
    {
        Directory.CreateDirectory(home);
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    using var keys = KeyRing.LoadOrCreate(home);
    var context = new DaemonContext(keys, home, config.ToOptions(), new ClientStore(ClientConfig.DatabasePath), LocalAddress.ForHome(home),
        k => KeyRing.SaveEncryption(home, k), SecretStores.ForHome(home));
    await using var app = DaemonHost.Build(context, windowsService: windowsService);
    await DaemonHost.StartAsync(app, context, cancel.Token);
    Console.Error.WriteLine($"* daemon for {keys.MachineId[..8]} ({home}) listening on {context.Address}; Ctrl+C to stop");
    await app.WaitForShutdownAsync(cancel.Token);
    return 0;
}

async Task<int> JoinAsync(string name, string? notes)
{
    using var api = await LocalApiClient.FindAsync(ClientConfig.HomeDirectory, cancel.Token);
    if (await api.IsRunningAsync(cancel.Token))
    {
        var result = await api.JoinAsync(name, notes, cancel.Token);
        if (result.Membership != "pending")
        {
            Console.WriteLine(result.Membership == "approved" ? $"already approved as {result.Name}" : $"this key was already {result.Membership}");
            return result.Membership == "approved" ? 0 : 1;
        }

        var router = await api.RouterAsync(cancel.Token);
        if (await OfferSelfApprovalAsync(router.RouterId, router.OwnerKey, result.MachineId) is { } selfApproval)
        {
            await api.ApproveOwnJoinAsync(selfApproval, cancel.Token);
        }
        else
        {
            PrintJoinInstructions(name, result.JoinCode, result.MachineId);
        }

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token);
            var status = await api.StatusAsync(cancel.Token);
            if (status.Membership != "pending")
            {
                Console.WriteLine(status.Membership == "approved" ? "approved" : $"not approved: {status.Membership}");
                return status.Membership == "approved" ? 0 : 1;
            }
        }
    }

    // No daemon yet: join directly with this home's keys.
    await using var client = await ConnectDirectAsync(probe: true);
    if (client.Status == MembershipStatus.Approved)
    {
        Console.WriteLine($"already approved as {client.Name}");
        return 0;
    }

    var approved = new TaskCompletionSource<MembershipStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.MembershipChanged += status => approved.TrySetResult(status);
    var asked = await client.RequestJoinAsync(name, MachineDescription.Detect(notes), cancel.Token);
    if (asked is MembershipStatus.Rejected or MembershipStatus.Approved)
    {
        Console.WriteLine($"this key was already {asked.ToString().ToLowerInvariant()}");
        return asked == MembershipStatus.Approved ? 0 : 1;
    }

    if (await OfferSelfApprovalAsync(client.RouterId, client.RouterOwnerKey, client.MachineId) is { } own)
    {
        await client.ApproveOwnJoinAsync(own, cancel.Token);
    }
    else
    {
        PrintJoinInstructions(name, client.JoinCode, client.MachineId);
    }

    var outcome = await approved.Task.WaitAsync(cancel.Token);
    Console.WriteLine(outcome == MembershipStatus.Approved ? "approved" : $"not approved: {outcome}");
    return outcome == MembershipStatus.Approved ? 0 : 1;
}

static void PrintJoinInstructions(string name, string joinCode, string machineId)
{
    Console.WriteLine($"asked to join as '{name}'. On the router, check this join code and approve:");
    Console.WriteLine($"    {joinCode}");
    Console.WriteLine($"    docker exec poniexp-server poniexp admin approve {machineId[..8]}");
    Console.WriteLine("waiting (Ctrl+C to stop; the request stays pending)...");
}

async Task<int> DoctorAsync()
{
    var failures = 0;
    void Check(bool ok, string what, string? fix = null)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {what}{(ok || fix is null ? string.Empty : $"\n      -> {fix}")}");
        failures += ok ? 0 : 1;
    }

    var config = ClientConfig.Load();
    Check(config is not null, config is null ? $"no config in {ClientConfig.HomeDirectory}" : $"router {config.RouterUrl}" +
        (config.CertificateSha256 is null ? " (no certificate pin)" : $" (pinned {config.CertificateSha256[..16]}...)"), "poniexp-client init <router-url> --pin <sha256>");
    using var api = await LocalApiClient.FindAsync(ClientConfig.HomeDirectory, cancel.Token);
    TimeSpan? offset;
    TimeSpan? skew;
    if (await api.IsRunningAsync(cancel.Token))
    {
        var status = await api.StatusAsync(cancel.Token);
        Check(true, $"daemon running ({api.Address}, home {status.Home})");
        Check(status.Reachable, status.Reachable ? $"daemon reaches {status.RouterUrl}" : $"daemon can't reach {status.RouterUrl}",
            "see 'poniexp-client diagnostics'; is the VPN up? is the router running?");
        Check(status.Membership == "approved", $"membership: {status.Membership}{(status.Name is null ? string.Empty : $" as {status.Name}")}",
            "poniexp-client join <name>");
        Check(status.Outbox == 0 || status.Connected, $"{status.Outbox} message(s) waiting in the outbox");
        (offset, skew) = (status.ClockOffset, status.RouterClockSkew);
        var recent = (await api.DiagnosticsAsync(cancel.Token)).Where(d => d.Level != "info" && d.OccurredOn > DateTimeOffset.UtcNow.AddHours(-24)).Take(5).ToList();
        if (recent.Count > 0)
        {
            // History: a failure only while it still matters (not connected); otherwise a note.
            var lines = "recent problems:\n" + string.Join("\n", recent.Select(d => $"      {d.OccurredOn.ToLocalTime():MM-dd HH:mm} {d.Category}: {d.Detail}"));
            if (status.Connected)
            {
                Console.WriteLine($"note  {lines}");
            }
            else
            {
                Check(false, lines);
            }
        }
    }
    else
    {
        Check(false, "daemon not running", "poniexp-client service install (or run 'poniexp-client daemon')");
        if (config is null)
        {
            return 1;
        }

        try
        {
            await using var client = await ConnectDirectAsync(probe: true);
            Check(true, $"router reachable, certificate {(config.CertificateSha256 is null ? "trusted" : "matches the pin")}, protocol {WireProtocol.Version}");
            Check(client.Status == MembershipStatus.Approved, $"membership: {WireProtocol.NameOf(client.Status)}", "poniexp-client join <name>");
            (offset, skew) = (client.ClockOffset, client.RouterClockSkew);
        }
        catch (Exception e) when (e is HttpRequestException or System.Net.WebSockets.WebSocketException or Microsoft.AspNetCore.SignalR.HubException
                                       or InvalidOperationException or IOException)
        {
            var pin = e.InnerException?.InnerException is System.Security.Authentication.AuthenticationException;
            var version = e is Microsoft.AspNetCore.SignalR.HubException && e.Message.Contains("protocol", StringComparison.Ordinal);
            Check(false, pin ? "the router's certificate doesn't match the pin"
                : version ? e.Message[(e.Message.IndexOf("This relay", StringComparison.Ordinal) is var at and >= 0 ? at : 0)..]
                : $"router unreachable: {e.Message}",
                pin ? "check the pin: docker exec poniexp-server poniexp admin certificate"
                : version ? "upgrade poniexp-client (or the router) so both speak the same protocol"
                : "is the VPN up? is the router running?");
            return 1;
        }
    }

    if (offset is { } o && skew is { } s)
    {
        Check(o.Duration() < s / 2, $"clock {FormatOffset(o)} from the router's (tolerance {s.TotalMinutes:0} min)",
            "sync this machine's clock (messages are refused when clocks differ by more than the tolerance)");
    }

    return failures == 0 ? 0 : 1;
}

async Task<int> ServiceInstallAsync(ServiceMode mode, bool dryRun)
{
    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("can't tell where poniexp-client is");
    if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
    {
        throw new InvalidOperationException("install from the published poniexp-client executable, not 'dotnet run'");
    }

    var ownerSid = OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value : null;
    var plan = ServiceInstaller.Install(mode, exe, ClientConfig.HomeDirectory, ownerSid);
    if (mode == ServiceMode.System && !dryRun && ClientConfig.Load() is null)
    {
        throw new InvalidOperationException("run 'poniexp-client init <router-url>' first: the service copies its router settings");
    }

    return await RunPlanAsync(plan, dryRun, () => mode == ServiceMode.User ? WaitForDaemonAsync() : Task.FromResult(true));
}

async Task<int> ServiceUninstallAsync(ServiceMode mode, bool dryRun)
{
    var plan = ServiceInstaller.Uninstall(mode);
    if (!dryRun && mode == ServiceMode.User && OperatingSystem.IsWindows())
    {
        using var api = new LocalApiClient(LocalAddress.ForHome(ClientConfig.HomeDirectory));
        if (await api.IsRunningAsync(cancel.Token))
        {
            await api.ShutdownAsync(cancel.Token);
        }
    }

    return await RunPlanAsync(plan, dryRun, () => Task.FromResult(true));
}

async Task<int> ServiceStatusAsync()
{
    foreach (var (label, home) in new[] { ("user", ClientConfig.HomeDirectory), ("system", LocalAddress.SystemHome) })
    {
        using var api = new LocalApiClient(LocalAddress.ForHome(home));
        Console.WriteLine($"{label,-7} daemon: {(await api.IsRunningAsync(cancel.Token) ? "running" : "not running")} ({api.Address})");
    }

    string[][] queries = OperatingSystem.IsWindows()
        ? [["reg", "query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", ServiceInstaller.Name], ["sc.exe", "query", ServiceInstaller.Name]]
        : OperatingSystem.IsMacOS() ? [["launchctl", "list", ServiceInstaller.LaunchdLabel]]
        : [["systemctl", "--user", "is-enabled", ServiceInstaller.SystemdUnit]];
    foreach (var q in queries)
    {
        var (code, _) = await RunAsync(q, quiet: true);
        Console.WriteLine($"{string.Join(' ', q.Take(3))}: {(code == 0 ? "installed" : "not installed")}");
    }

    return 0;
}

async Task<int> RunPlanAsync(ServicePlan plan, bool dryRun, Func<Task<bool>> verify)
{
    Console.WriteLine(plan.Description);
    foreach (var (from, to) in plan.Copies)
    {
        Console.WriteLine($"  copy  {from} -> {to}");
    }

    foreach (var file in plan.Files)
    {
        Console.WriteLine($"  write {file.Path}");
    }

    foreach (var command in plan.Commands)
    {
        Console.WriteLine($"  run   {ServiceInstaller.Quote(command)}");
    }

    if (plan.StartDetached is { } start)
    {
        Console.WriteLine($"  start {ServiceInstaller.Quote(start)}");
    }

    if (plan.Notes is { } notes)
    {
        Console.WriteLine($"note: {notes}");
    }

    if (dryRun)
    {
        return 0;
    }

    if (plan.NeedsAdmin && !IsAdmin())
    {
        throw new InvalidOperationException(OperatingSystem.IsWindows() ? "this needs an elevated (administrator) prompt" : "this needs sudo");
    }

    foreach (var (from, to) in plan.Copies)
    {
        Copy(from, to);
    }

    foreach (var file in plan.Files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        await File.WriteAllTextAsync(file.Path, file.Content, cancel.Token);
    }

    foreach (var command in plan.Commands)
    {
        var (code, output) = await RunAsync(command, quiet: false);
        if (code != 0 && !command.Contains("bootout"))
        {
            throw new InvalidOperationException($"'{string.Join(' ', command.Take(2))}' failed ({code}): {output.Trim()}");
        }
    }

    if (plan.StartDetached is { } detached)
    {
        // Shell-executed, so it inherits none of this process's handles (a redirected stdout would stay open).
        Process.Start(new ProcessStartInfo(detached[0], detached[1..]) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
    }

    Console.WriteLine(await verify() ? "done" : "installed, but the daemon didn't answer yet; see 'poniexp-client doctor'");
    return 0;
}

async Task<int> UpdateAsync(string newBinary, ServiceMode mode)
{
    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("can't tell where poniexp-client is");
    if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
    {
        throw new InvalidOperationException("run update from the installed poniexp-client, not 'dotnet run'");
    }

    var plan = ServiceInstaller.Control(mode, exe, ClientConfig.HomeDirectory);
    if (plan.NeedsAdmin && !IsAdmin())
    {
        throw new InvalidOperationException(OperatingSystem.IsWindows() ? "this needs an elevated (administrator) prompt" : "this needs sudo");
    }

    Console.WriteLine($"updating {plan.InstalledExe} from {Path.GetFullPath(newBinary)}");
    var address = LocalAddress.ForHome(mode == ServiceMode.User ? ClientConfig.HomeDirectory : LocalAddress.SystemHome);
    var updater = new ClientUpdater(new ServiceControl(plan, address, Console.Out), Console.Out);
    var version = await updater.UpdateAsync(newBinary, plan.InstalledExe, cancel.Token);
    Console.WriteLine($"done: the daemon answers, version {version}");
    return 0;
}

int Version()
{
    Console.WriteLine(ClientVersion.Current);
    return 0;
}

async Task<bool> WaitForDaemonAsync()
{
    using var api = new LocalApiClient(LocalAddress.ForHome(ClientConfig.HomeDirectory));
    for (var i = 0; i < 20; i++)
    {
        if (await api.IsRunningAsync(cancel.Token))
        {
            return true;
        }

        await Task.Delay(500, cancel.Token);
    }

    return false;
}

// ---- messaging ----

async Task<int> StatusAsync()
{
    using var api = await DaemonAsync2();
    var s = await api.StatusAsync(cancel.Token);
    Console.WriteLine($"{s.Membership}{(s.Name is null ? string.Empty : $" as {s.Name}")} ({s.MachineId[..8]}), " +
                      $"{(s.Connected ? "connected" : s.Reachable ? "reaching" : "not connected")} to {s.RouterUrl}{(s.Outbox > 0 ? $", {s.Outbox} in the outbox" : string.Empty)}");
    Console.WriteLine("agents:");
    foreach (var e in s.Agents)
    {
        Console.WriteLine($"  {e.Name,-20} {e.State,-10} since {e.ChangedOn.ToLocalTime():yyyy-MM-dd HH:mm}");
    }

    var machines = await api.MachinesAsync(cancel.Token);
    var names = machines.ToDictionary(m => m.Id, m => m.Name);
    if (s.Metrics.Count > 0)
    {
        Console.WriteLine("metrics:");
        foreach (var metric in s.Metrics)
        {
            Console.WriteLine($"  {names.GetValueOrDefault(metric.PeerMachine, metric.PeerMachine[..8]),-16} {metric.Name,-16} " +
                              $"{metric.Count,6}  last {metric.LastOn.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
    }

    return s.Membership == "approved" ? 0 : 1;
}

async Task<int> MachinesAsync()
{
    using var api = await DaemonAsync2();
    foreach (var m in await api.MachinesAsync(cancel.Token))
    {
        var seen = m.Online ? "online" : m.LastSeenOn is { } on ? $"last seen {on.ToLocalTime():yyyy-MM-dd HH:mm}" : "never seen";
        Console.WriteLine($"{m.Name,-16} {m.Id[..8]}  {seen,-28} {m.OperatingSystem}" + (m.Capabilities is { } c ? $"\n  {c.Summary()}" : ""));
        foreach (var e in m.Agents)
        {
            Console.WriteLine($"  {m.Name}/{e.Name,-20} {e.State}");
        }
    }

    return 0;
}

async Task<int> SendAsync(string[] rest)
{
    var to = new List<string>();
    var files = new List<string>();
    var metadata = new Dictionary<string, string>();
    string from = Address.DefaultAgent;
    string? subject = null, contentType = null, body = null;
    Guid? conversation = null, operation = null;
    TimeSpan? expires = null;
    for (var i = 0; i < rest.Length; i++)
    {
        string Next() => i + 1 < rest.Length ? rest[++i] : throw new ArgumentException($"{rest[i]} needs a value.");
        switch (rest[i])
        {
            case "--to": to.Add(Next()); break;
            case "--from": from = Next(); break;
            case "--subject": subject = Next(); break;
            case "--conversation": conversation = Guid.Parse(Next()); break;
            case "--operation": operation = Guid.Parse(Next()); break;
            case "--expires": expires = ParseDuration(Next()); break;
            case "--content-type": contentType = Next(); break;
            case "--meta": AddMeta(metadata, Next()); break;
            case "--attach": files.Add(Path.GetFullPath(Next())); break;
            case var option when option.StartsWith("--", StringComparison.Ordinal) && option != "-":
                throw new ArgumentException($"unknown option {option}");
            default:
                body = body is null ? rest[i] : throw new ArgumentException("give the body once (quote it), or \"-\" for stdin");
                break;
        }
    }

    if (to.Count == 0 || body is null)
    {
        return Fail(Usage);
    }

    using var api = await DaemonAsync2();
    var sent = await api.SendAsync(new LocalSendRequest(from, to, body == "-" ? await Console.In.ReadToEndAsync() : body, subject, conversation,
        OperationId: operation, ContentType: contentType, Metadata: metadata.Count == 0 ? null : metadata, ExpiresIn: expires,
        Attachments: files.Count == 0 ? null : files), cancel.Token);
    foreach (var s in sent)
    {
        Console.WriteLine($"{s.State}: {s.Id} to {s.To} (#{s.Sequence}, conversation {s.ConversationId.ToString()[..8]}, " +
                          $"expires {s.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm})");
    }

    return 0;
}

async Task<int> InboxAsync(string agent, bool json)
{
    using var api = await DaemonAsync2();
    var messages = await api.InboxAsync(agent, cancel.Token);
    foreach (var m in messages)
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(m, WireProtocol.Json) : Format(m));
    }

    if (!json && messages.Count == 0)
    {
        Console.Error.WriteLine($"* nothing waiting for {agent}");
    }

    return 0;
}

async Task<int> AckAsync(string agent, Guid id)
{
    using var api = await DaemonAsync2();
    var m = await api.AckAsync(agent, id, cancel.Token);
    Console.WriteLine($"acknowledged {m.Id} from {m.From}");
    return 0;
}

async Task<int> ReplyAsync(Guid id, string[] rest)
{
    var metadata = new Dictionary<string, string>();
    var files = new List<string>();
    string agent = Address.DefaultAgent;
    string? status = null, body = null;
    for (var i = 0; i < rest.Length; i++)
    {
        string Next() => i + 1 < rest.Length ? rest[++i] : throw new ArgumentException($"{rest[i]} needs a value.");
        switch (rest[i])
        {
            case "--agent": agent = Next(); break;
            case "--status": status = Next(); break;
            case "--meta": AddMeta(metadata, Next()); break;
            case "--attach": files.Add(Path.GetFullPath(Next())); break;
            case var option when option.StartsWith("--", StringComparison.Ordinal) && option != "-":
                throw new ArgumentException($"unknown option {option}");
            default:
                body = body is null ? rest[i] : throw new ArgumentException("give the body once (quote it), or \"-\" for stdin");
                break;
        }
    }

    if (body is null)
    {
        return Fail(Usage);
    }

    using var api = await DaemonAsync2();
    var sent = await api.ReplyAsync(agent, new LocalReplyRequest(id, body == "-" ? await Console.In.ReadToEndAsync() : body, status,
        Metadata: metadata.Count == 0 ? null : metadata, Attachments: files.Count == 0 ? null : files), cancel.Token);
    Console.WriteLine($"{sent.State}: {sent.Id} to {sent.To} (#{sent.Sequence})");
    return 0;
}

async Task<int> MessageAsync(string agent, Guid id)
{
    using var api = await DaemonAsync2();
    var detail = await api.MessageAsync(agent, id, cancel.Token);
    Console.WriteLine(Format(detail.Message));
    foreach (var e in detail.Events)
    {
        Console.WriteLine($"  {e.OccurredOn.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {e.Event}{(e.Detail is null ? string.Empty : $" ({e.Detail})")}");
    }

    return 0;
}

async Task<int> ConversationAsync(string agent, Guid id, bool json)
{
    using var api = await DaemonAsync2();
    foreach (var m in await api.ConversationAsync(agent, id, cancel.Token))
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(m, WireProtocol.Json) : Format(m));
    }

    return 0;
}

async Task<int> ListenAsync(string agent, bool json, bool ack, bool autoFetch)
{
    using var api = await DaemonAsync2();
    await using var connection = new AgentConnection(api, agent, async e =>
    {
        if (e.Kind == LocalEventKinds.Message && e.Message is { } m)
        {
            Console.WriteLine(json ? JsonSerializer.Serialize(m, WireProtocol.Json) : Format(m));
            if (autoFetch && m.Attachments is { Count: > 0 })
            {
                try
                {
                    foreach (var a in await api.FetchAsync(agent, new LocalFileRequest(m.Id), cancel.Token))
                    {
                        Console.Error.WriteLine($"* fetched {a.Name}: {a.Path}");
                    }
                }
                catch (LocalApiException failure)
                {
                    Console.Error.WriteLine($"* couldn't fetch {m.Id.ToString()[..8]}'s files: {failure.Message}");
                }
            }

            if (ack)
            {
                await api.AckAsync(agent, m.Id, cancel.Token);
            }
        }
        else if (e.Kind == LocalEventKinds.RoomMessage && e.Message is { } rm)
        {
            Console.WriteLine(json ? JsonSerializer.Serialize(e, WireProtocol.Json) : Format(rm));
        }
        else if (e.Kind is LocalEventKinds.JobReady or LocalEventKinds.JobChanged && e.Job is { } job)
        {
            Console.WriteLine(json ? JsonSerializer.Serialize(e, WireProtocol.Json)
                : e.Kind == LocalEventKinds.JobReady ? $"job ready: {job.Id.ToString()[..8]} {job.Title}" + (job.Requirements.Count > 0 ? $" [{string.Join(", ", job.Requirements)}]" : "") + $" (job claim {job.Id.ToString()[..8]} --as <agent>)"
                : $"job {job.Id.ToString()[..8]} {job.Title}: " + (job.History.LastOrDefault() is { } h
                    ? $"{h.By} {h.What}" + (h.Text is { Length: > 0 } t && h.What != "posted" ? $": {t}" : "") : job.Column));
        }
        else if (e.Kind == LocalEventKinds.RoomInvite && e.Invite is { } invite)
        {
            Console.WriteLine(json ? JsonSerializer.Serialize(e, WireProtocol.Json)
                : $"invited to room {invite.Room} by {invite.InvitedBy} (room join {invite.Room} --as <agent>; open until {invite.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm})");
        }
        else if (e.Kind == LocalEventKinds.RoomChanged && e.Room is { } changed)
        {
            Console.WriteLine(json ? JsonSerializer.Serialize(e, WireProtocol.Json)
                : changed.Collapsed ? $"room {changed.Name} collapsed ({changed.CollapseReason})" : $"room {changed.Name}: {string.Join(", ", changed.Members)}");
        }
        else if (e.FileReceipt is { } f)
        {
            Console.Error.WriteLine(json ? JsonSerializer.Serialize(f, WireProtocol.Json)
                : $"* file {f.Receipt} by {f.From}: {f.BlobId.ToString()[..8]} (with {f.MessageId.ToString()[..8]}){(f.Reason is null ? "" : $": {f.Reason}")}");
        }
        else if (e.Receipt is { } r)
        {
            Console.Error.WriteLine(json ? JsonSerializer.Serialize(r, WireProtocol.Json)
                : $"* {r.Receipt} by {r.From}: {r.MessageId.ToString()[..8]}{(r.Reason is null ? string.Empty : $" ({r.Reason})")}");
        }
    }, onError: e => Console.Error.WriteLine($"* {e.Message}; retrying"));
    await connection.Registered.WaitAsync(TimeSpan.FromSeconds(30), cancel.Token);
    Console.Error.WriteLine($"* listening as {agent}; Ctrl+C to stop");
    try
    {
        await Task.Delay(Timeout.Infinite, cancel.Token);
    }
    catch (OperationCanceledException)
    {
    }

    return 0;
}

async Task<int> ConversationsAsync(string agent)
{
    using var api = await DaemonAsync2();
    foreach (var c in await api.ConversationsAsync(agent, cancel.Token))
    {
        Console.WriteLine($"{c.Id.ToString()[..8]}  {c.LastActivityOn.ToLocalTime():yyyy-MM-dd HH:mm}  {c.State,-8}  {c.Peer,-28}  {c.Subject}");
    }

    return 0;
}

async Task<int> LogAsync(string[] rest)
{
    Guid? conversation = null, operation = null;
    string? peer = null, agent = null, export = null;
    DateTimeOffset? since = null;
    int? limit = null;
    bool json = false, bodies = false, automatic = false;
    for (var i = 0; i < rest.Length; i++)
    {
        string Next() => ++i < rest.Length ? rest[i] : throw new ArgumentException($"{rest[i - 1]} needs a value");
        switch (rest[i])
        {
            case "--conversation": conversation = Guid.Parse(Next()); break;
            case "--operation": operation = Guid.Parse(Next()); break;
            case "--peer": peer = Next(); break;
            case "--agent": agent = Next(); break;
            case "--since":
                var value = Next();
                since = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var on) ? on
                    : DateTimeOffset.UtcNow - ParseDuration(value);
                break;
            case "--limit": limit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--json": json = true; break;
            case "--export": export = Next(); json = true; break;
            case "--bodies": bodies = true; break;
            case "--automatic": automatic = true; break;
            default: throw new ArgumentException($"unknown log option {rest[i]}");
        }
    }

    using var api = await DaemonAsync2();
    var entries = await api.LogAsync(new LocalLogQuery(conversation, operation, peer, agent, since, limit, automatic, bodies), cancel.Token);
    using var file = export is null or "-" ? null : new StreamWriter(PrivateFiles.Create(export));
    var output = file ?? Console.Out;
    foreach (var e in entries)
    {
        output.WriteLine(json ? JsonSerializer.Serialize(e, WireProtocol.Json)
            : $"{e.StoredOn.ToLocalTime():yyyy-MM-dd HH:mm:ss} {e.Id.ToString()[..8]} {e.From} -> {e.To} {e.Type}" +
              $"{(e.Status is null ? string.Empty : $" [{e.Status}]")} <{e.State ?? "?"}> conv {e.ConversationId.ToString()[..8]}" +
              $"{(e.Subject is null ? string.Empty : $" \"{e.Subject}\"")}{(e.Body is null ? (bodies && e.BodyDropped ? " (body dropped by retention)" : string.Empty) : $": {e.Body}")}");
    }

    if (file is not null)
    {
        Console.Error.WriteLine($"wrote {entries.Count} entries to {export}{(bodies ? " (with decrypted bodies; owner-only)" : string.Empty)}");
    }

    return 0;
}

async Task<int> JobAsync(string[] rest)
{
    using var api = await DaemonAsync2();
    string As() => Option(rest, "--as") ?? throw new ArgumentException("say which agent you are: --as <agent>");
    var positional = new List<string>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i] is "--as" or "--description" or "--require" or "--column" or "--note")
        {
            i++;
        }
        else if (rest[i] is not ("--all" or "--backlog" or "--json"))
        {
            positional.Add(rest[i]);
        }
    }

    void Show(LocalJob j, bool full = false)
    {
        if (rest.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(j, WireProtocol.Json));
            return;
        }

        Console.WriteLine($"{j.Id.ToString()[..8]}  {j.Column,-8} {j.Title}" + (j.Requirements.Count > 0 ? $"  [{string.Join(", ", j.Requirements)}]" : "") +
                          (j.Unmet.Count > 0 ? $"  (this machine lacks {string.Join(", ", j.Unmet)})" : "") +
                          $"\n    posted by {j.Poster} {j.PostedOn.ToLocalTime():yyyy-MM-dd HH:mm}" + (j.Claimant is { } who ? $", claimed by {who}" : "") +
                          (j.Result is { Length: > 0 } result ? $"\n    result: {result}" : ""));
        if (full)
        {
            if (j.Description.Length > 0)
            {
                Console.WriteLine("\n" + j.Description + "\n");
            }

            foreach (var h in j.History)
            {
                Console.WriteLine($"    [{h.OccurredOn.ToLocalTime():yyyy-MM-dd HH:mm}] {h.By} {h.What}{(h.Column is { } c && h.What != c ? $" ({c})" : "")}" +
                                  (h.Text is { Length: > 0 } text && h.What != "posted" ? $": {text}" : ""));
            }
        }
    }

    switch (positional)
    {
        case ["list"]:
            var jobs = await api.JobsAsync(rest.Contains("--all"), Option(rest, "--column"), cancel.Token);
            if (jobs.Count == 0)
            {
                Console.WriteLine("the board is empty");
            }

            jobs.ForEach(j => Show(j));
            return 0;
        case ["show", var id]:
            Show(await api.JobAsync(id, cancel.Token), full: true);
            return 0;
        case ["post", var title]:
            var description = Option(rest, "--description") is "-" ? await Console.In.ReadToEndAsync() : Option(rest, "--description");
            Show(await api.PostJobAsync(new LocalJobPost(As(), title, description, Options(rest, "--require"), rest.Contains("--backlog")), cancel.Token));
            return 0;
        case [var verb and ("claim" or "release" or "cancel"), var id]:
            Show(await api.JobActionAsync(id, verb, new LocalJobAction(As(), Text: Option(rest, "--note")), cancel.Token));
            return 0;
        case ["move", var id, var column]:
            Show(await api.JobActionAsync(id, "move", new LocalJobAction(As(), column, Option(rest, "--note")), cancel.Token));
            return 0;
        case ["comment", var id, var text]:
            Show(await api.JobActionAsync(id, "comment", new LocalJobAction(As(), Text: text), cancel.Token));
            return 0;
        default:
            return Fail(Usage);
    }
}

/// <summary>
/// The router has an owner key and this machine's SSH agent holds it: offer to approve this machine with it (one prompt).
/// Null if not (then it waits for the desk, or docker exec).
/// </summary>
async Task<PoniExp.Core.Protocol.AdminCommand?> OfferSelfApprovalAsync(string? routerId, string? ownerKey, string machineId)
{
    if (routerId is null || ownerKey is null || await AgentHoldsAsync(ownerKey) is not { } key)
    {
        return null;
    }

    Console.Write($"Your SSH agent holds the router's owner key ({key.Fingerprint}). Approve this machine with it? [Y/n] ");
    if (Console.ReadLine()?.Trim().ToLowerInvariant() is "n" or "no")
    {
        return null;
    }

    Console.WriteLine("Approve the signing request in your SSH agent (e.g. Bitwarden)...");
    var id = Guid.NewGuid();
    var issued = DateTimeOffset.UtcNow;
    string[] args = ["approve", machineId];
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
    timeout.CancelAfter(TimeSpan.FromMinutes(2));
    var signature = await new PoniExp.Core.Crypto.SshAgent().SignAsync(key, PoniExp.Core.Protocol.AdminCommand.Payload(id, routerId, args, null, issued), timeout.Token);
    return new PoniExp.Core.Protocol.AdminCommand(id, routerId, args, null, issued, signature);
}

// ---- wake (phase 12) ----

async Task<int> WakeListAsync()
{
    using var api = await DaemonAsync2();
    var targets = await api.WakeTargetsAsync(cancel.Token);
    if (targets.Count == 0)
    {
        Console.WriteLine("no wake targets (wake set <agent> --a2a <url> | --command <program>)");
    }

    foreach (var t in targets)
    {
        PrintWake(t);
    }

    return 0;
}

async Task<int> WakeSetAsync(string agent, string[] rest)
{
    LocalWakeSet request;
    if (Option(rest, "--a2a") is { } url)
    {
        string? token = null;
        if (rest.Contains("--token"))
        {
            token = ReadHidden($"Peer token for {url} (one of its A2A_PEER_TOKENS; not shown): ");
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("no token given");
                return 2;
            }
        }

        request = new LocalWakeSet(url, null, null, token?.Trim());
    }
    else if (Array.IndexOf(rest, "--command") is var at and >= 0 && at + 1 < rest.Length)
    {
        request = new LocalWakeSet(null, rest[at + 1], [.. rest.Skip(at + 2)], null);
    }
    else
    {
        Console.Error.WriteLine("wake set <agent> --a2a <url> [--token] | --command <program> [args...]");
        return 2;
    }

    using var api = await DaemonAsync2();
    PrintWake(await api.SetWakeAsync(agent, request, cancel.Token));
    Console.WriteLine($"Try it: poniexp-client wake test {agent}");
    return 0;
}

async Task<int> WakeTestAsync(string agent)
{
    using var api = await DaemonAsync2();
    PrintWake(await api.TestWakeAsync(agent, cancel.Token));
    Console.WriteLine("nudge delivered");
    return 0;
}

async Task<int> WakeRemoveAsync(string agent)
{
    using var api = await DaemonAsync2();
    await api.RemoveWakeAsync(agent, cancel.Token);
    Console.WriteLine($"{agent} is no longer woken");
    return 0;
}

static void PrintWake(LocalWakeTarget t)
{
    var token = t.Kind == "a2a" ? (t.HasToken ? ", with a token" : ", no token") : "";
    Console.WriteLine($"{t.Agent,-16} {t.Kind,-8} {t.Target}{token}");
    Console.WriteLine($"{"",-16} {t.Waiting} waiting; last nudge {(t.LastNudge is { } n ? n.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "never")}"
        + (t.LastError is { } e ? $"; last failure {t.LastFailure!.Value.ToLocalTime():MM-dd HH:mm:ss}: {e}" : ""));
}

/// <summary>A secret typed at the console without echo; from stdin as one line when it's redirected.</summary>
static string? ReadHidden(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine();
    }

    var text = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.Error.WriteLine();
            return text.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0)
            {
                text.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            text.Append(key.KeyChar);
        }
    }
}

// ---- keys ----

async Task<int> KeysAsync(bool rotate)
{
    using var api = await DaemonAsync2();
    var keys = rotate ? await api.RotateKeysAsync(cancel.Token) : await api.KeysAsync(cancel.Token);
    Console.WriteLine($"{(rotate ? "rotated: " : "")}encryption key issued {keys.IssuedOn.ToLocalTime():yyyy-MM-dd HH:mm}, next rotation {keys.DueOn.ToLocalTime():yyyy-MM-dd}" +
                      (keys.Retired.Count > 0 ? $"; {keys.Retired.Count} retired key(s) kept to open older messages" : ""));
    return 0;
}

/// <summary>
/// `keys reset`: for a machine whose keys were revoked (or may have leaked). The daemon must be stopped. The old
/// database and keys move to a folder beside them (the old messages stay readable there); new keys are made; the
/// machine then joins again, under the same name.
/// </summary>
async Task<int> KeysResetAsync()
{
    var home = ClientConfig.HomeDirectory;
    using (var probe = await LocalApiClient.FindAsync(home, cancel.Token))
    {
        if (await probe.IsRunningAsync(cancel.Token))
        {
            return Fail("stop the daemon first (poniexp-client service uninstall, or Ctrl+C on `poniexp-client daemon`)");
        }
    }

    Console.Write("This makes new keys: this machine becomes a new machine to the router, and must be approved again. Continue? [y/N] ");
    if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
    {
        return Fail("nothing changed");
    }

    var archive = Path.Combine(home, $"reset-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
    Directory.CreateDirectory(archive);
    foreach (var file in Directory.GetFiles(Path.GetDirectoryName(ClientConfig.DatabasePath)!, Path.GetFileName(ClientConfig.DatabasePath) + "*"))
    {
        File.Move(file, Path.Combine(archive, Path.GetFileName(file)));
    }

    var store = SecretStores.ForHome(home);
    var kept = SecretStores.ForHome(archive);
    foreach (var name in new[] { KeyRing.IdentityName, KeyRing.EncryptionName, KeyRing.EncryptionIssuedName, KeyRing.RetiredName })
    {
        if (store.Load(name) is { } secret)
        {
            kept.Save(name, secret);
            store.Delete(name);
        }
    }

    using var keys = KeyRing.LoadOrCreate(home);
    Console.WriteLine($"New keys: machine {keys.MachineId[..8]}, join code {KeyFingerprint.JoinCodeOf(keys.PublicKey)}. The old data is in {archive}.");
    Console.WriteLine("Start the daemon and join again under the same name: poniexp-client join <name> (the desk shows it replaces the old machine).");
    return 0;
}

// ---- the owner, the desk and approvals ----

static void ShowOwner(LocalOwner o) => Console.WriteLine(
    $"router's owner key  {(o.RouterKey is null ? "none (poniexp admin owner set)" : $"{o.RouterFingerprint}  {o.RouterKey.Split(' ', 3).ElementAtOrDefault(2)}")}\n" +
    $"pinned here         {(o.PinnedKey is null ? "none (poniexp-client owner pin)" : $"{o.PinnedFingerprint}{(o.RouterKey is not null && o.RouterFingerprint != o.PinnedFingerprint ? "  DIFFERS FROM THE ROUTER'S" : "")}")}\n" +
    $"desk                {(o.DeskMachine is null ? "none" : $"{o.DeskName}{(o.IsDesk ? " (this machine)" : "")} until {o.DeskUntil?.ToLocalTime():yyyy-MM-dd HH:mm}")}");

async Task<int> OwnerAsync()
{
    using var api = await DaemonAsync2();
    ShowOwner(await api.OwnerAsync(cancel.Token));
    return 0;
}

/// <summary>The SSH agent here holds the owner's key, or null (no agent, or not that key).</summary>
async Task<PoniExp.Core.Crypto.OwnerKey?> AgentHoldsAsync(string line)
{
    var wanted = PoniExp.Core.Crypto.OwnerKey.Parse(line);
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return (await new PoniExp.Core.Crypto.SshAgent().KeysAsync(timeout.Token)).FirstOrDefault(k => k.SameKeyAs(wanted));
    }
    catch (PoniExp.Core.Crypto.OwnerKeyUnavailableException)
    {
        return null;
    }
}

async Task<int> PinOwnerAsync()
{
    using var api = await DaemonAsync2();
    var owner = await api.OwnerAsync(cancel.Token);
    if (owner.RouterKey is null)
    {
        return Fail("the router has no owner key yet: docker exec poniexp-server poniexp admin owner set \"<your ssh public key>\"");
    }

    var key = PoniExp.Core.Crypto.OwnerKey.Parse(owner.RouterKey);
    if (await AgentHoldsAsync(owner.RouterKey) is not null)
    {
        Console.WriteLine($"Your SSH agent holds this key ({key.Fingerprint} {key.Comment}): pinning it.");
    }
    else
    {
        Console.WriteLine($"The router says the owner key is:\n  {key.Fingerprint}  {key.Comment}\n" +
                          "Compare it with `ssh-add -l` on a machine whose SSH agent holds your key (or Bitwarden's SSH key item).");
        Console.Write("Pin it on this machine? [y/N] ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
        {
            return Fail("not pinned");
        }
    }

    ShowOwner(await api.PinOwnerAsync(owner.RouterKey, cancel.Token));
    return 0;
}

async Task<PoniExp.Core.Protocol.HereClaim> SignClaimAsync(PoniExp.Core.Crypto.OwnerKey key, string? machine, DateTimeOffset issued, DateTimeOffset expires)
{
    Console.WriteLine("Approve the signing request in your SSH agent (e.g. Bitwarden)...");
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
    timeout.CancelAfter(TimeSpan.FromMinutes(2));
    var payload = PoniExp.Core.Protocol.HereClaim.Payload(machine, issued, expires);
    return new PoniExp.Core.Protocol.HereClaim(machine, issued, expires, await new PoniExp.Core.Crypto.SshAgent().SignAsync(key, payload, timeout.Token));
}

async Task<int> HereAsync(TimeSpan? lifetime)
{
    using var api = await DaemonAsync2();
    var owner = await api.OwnerAsync(cancel.Token);
    if (owner.PinnedKey is null)
    {
        if (owner.RouterKey is null || await AgentHoldsAsync(owner.RouterKey) is null)
        {
            return Fail("no owner key is pinned here: poniexp-client owner pin");
        }

        owner = await api.PinOwnerAsync(owner.RouterKey, cancel.Token); // the SSH agent here holds it: nothing to compare
        Console.WriteLine($"Pinned the owner key {owner.PinnedFingerprint} (your SSH agent holds it).");
    }

    if (await AgentHoldsAsync(owner.PinnedKey!) is not { } key)
    {
        return Fail($"the SSH agent here doesn't hold the owner key {owner.PinnedFingerprint} (is Bitwarden unlocked, with its SSH agent on?)");
    }

    var span = lifetime ?? owner.HereLifetime;
    if (span <= TimeSpan.Zero || span > PoniExp.Core.Protocol.HereClaim.MaxLifetime)
    {
        return Fail("a desk lasts at most 7 days");
    }

    var now = DateTimeOffset.UtcNow;
    try
    {
        var claim = await SignClaimAsync(key, owner.MachineId, now, now + span);
        ShowOwner(await api.HereAsync(claim, cancel.Token));
        return 0;
    }
    catch (PoniExp.Core.Crypto.OwnerKeyUnavailableException e)
    {
        return Fail($"not signed: {e.Message}");
    }
}

async Task<int> AwayAsync()
{
    using var api = await DaemonAsync2();
    var owner = await api.OwnerAsync(cancel.Token);
    if (owner.IsDesk)
    {
        ShowOwner(await api.AwayAsync(null, cancel.Token));
        return 0;
    }

    if (owner.DeskMachine is null)
    {
        Console.WriteLine("there's no desk");
        return 0;
    }

    if (owner.PinnedKey is null || await AgentHoldsAsync(owner.PinnedKey) is not { } key)
    {
        return Fail($"the desk is {owner.DeskName}: run `away` there, or here with your SSH agent holding the owner key");
    }

    var now = DateTimeOffset.UtcNow;
    ShowOwner(await api.AwayAsync(await SignClaimAsync(key, null, now, now), cancel.Token));
    return 0;
}

static void ShowApproval(LocalApproval a) => Console.WriteLine(
    $"{a.Id.ToString()[..8]}  {a.From}  {a.RequestedOn.ToLocalTime():MM-dd HH:mm}  {a.State}{(a.Always ? " (always)" : "")}{(a.AnsweredBy is { } by ? $" by {by}" : "")}" +
    $"{(a.Reason is { } why ? $" ({why})" : "")}\n    {a.Summary}" + (a.Command is { } c && a.Direction is not ("join" or "admin") && !a.Summary.Contains(c, StringComparison.Ordinal) ? $"\n    $ {c}" : "") +
    (a.Details is { Length: > 0 } d && d != a.Command ? $"\n    {d.ReplaceLineEndings("\n    ")}" : ""));

async Task<int> ApprovalsAsync(bool mine)
{
    using var api = await DaemonAsync2();
    if (mine)
    {
        var asked = await api.ApprovalsAsync(cancel.Token);
        Console.WriteLine(asked.Count == 0 ? "no approvals asked from here" : "");
        asked.ForEach(ShowApproval);
        return 0;
    }

    var owner = await api.OwnerAsync(cancel.Token);
    if (!owner.IsDesk)
    {
        Console.WriteLine($"this machine isn't the desk ({(owner.DeskName is { } d ? $"the desk is {d}" : "there's none: poniexp-client here")})");
        return 0;
    }

    var waiting = await api.DeskAsync(cancel.Token);
    Console.WriteLine(waiting.Count == 0 ? "nothing waits" : $"{waiting.Count} waiting (approve <id> [--always] | deny <id> [--reason <text>]):");
    waiting.ForEach(ShowApproval);
    return 0;
}

/// <summary>
/// `admin ...`: the command, signed by the owner key through the SSH agent, run on the router; `backup <file>` and
/// `restore <file>` read and write the file here.
/// </summary>
async Task<int> RemoteAdminAsync(string[] args)
{
    using var api = await DaemonAsync2();
    return await RunSignedAdminAsync(api, args);
}

async Task<int> RunSignedAdminAsync(LocalApiClient api, string[] args, PoniExp.Core.Protocol.AdminRequest? approving = null)
{
    var owner = await api.OwnerAsync(cancel.Token);
    if (owner.RouterId is null)
    {
        return Fail("the daemon isn't connected to the router");
    }

    if (owner.PinnedKey is null || await AgentHoldsAsync(owner.PinnedKey) is not { } key)
    {
        return Fail(owner.PinnedKey is null ? "no owner key is pinned here: poniexp-client owner pin"
            : $"the SSH agent here doesn't hold the owner key {owner.PinnedFingerprint} (is Bitwarden unlocked, with its SSH agent on?)");
    }

    string? input = null;
    string? backupTo = null;
    var sent = args.ToList();
    if (approving is not null)
    {
        // a docker exec change: exactly its arguments; the router holds its input
    }
    else if (args is ["revoke", var who] && !(who.Length == 32 && who.All(Uri.IsHexDigit)))
    {
        var found = (await api.MachinesAsync(cancel.Token)).Where(m => m.Name == who || (who.Length >= 4 && m.Id.StartsWith(who, StringComparison.Ordinal))).ToList();
        if (found is not [var one])
        {
            return Fail(found.Count == 0 ? $"no machine matches '{who}'" : $"'{who}' matches {found.Count} machines; use the id");
        }

        sent[1] = one.Id; // the owner signs the full id, which every machine checks
        Console.WriteLine($"Revoking {one.Name} ({one.Id[..8]}): its key is refused for good.");
    }
    else if (args is ["backup", var to] && to != "-")
    {
        (sent[1], backupTo) = ("-", to);
    }
    else if (args is ["restore", var from])
    {
        input = from == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(from);
        sent[1] = "-";
    }

    var id = Guid.NewGuid();
    var issued = DateTimeOffset.UtcNow;
    Console.Error.WriteLine("Approve the signing request in your SSH agent (e.g. Bitwarden)...");
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
    timeout.CancelAfter(TimeSpan.FromMinutes(2));
    var payload = PoniExp.Core.Protocol.AdminCommand.Payload(id, owner.RouterId, sent, input, issued, approving?.Id, approving?.InputSha256);
    string signature;
    try
    {
        signature = await new PoniExp.Core.Crypto.SshAgent().SignAsync(key, payload, timeout.Token);
    }
    catch (PoniExp.Core.Crypto.OwnerKeyUnavailableException e)
    {
        return Fail($"not signed: {e.Message}");
    }

    var result = await api.AdminAsync(new PoniExp.Core.Protocol.AdminCommand(id, owner.RouterId, sent, input, issued, signature, approving?.Id,
        approving?.InputSha256), cancel.Token);
    if (backupTo is not null && result.ExitCode == 0)
    {
        PoniExp.Core.Crypto.PrivateFiles.WriteAllText(backupTo, result.Output);
        Console.WriteLine($"backup written to {backupTo} (it holds the router's TLS and signing private keys: keep it safe)");
    }
    else
    {
        Console.Write(result.Output);
    }

    return result.ExitCode;
}

async Task<int> AnswerAsync(string id, bool approve, bool always, string? reason)
{
    using var api = await DaemonAsync2();
    var join = (await api.DeskAsync(cancel.Token)).Where(a => a.Direction == "join" && (a.Id.ToString() == id || (id.Length >= 4 && a.Id.ToString().StartsWith(id, StringComparison.Ordinal))))
        .ToList();
    var change = (await api.DeskAsync(cancel.Token)).Where(a => a.Direction == "admin" && (a.Id.ToString() == id || (id.Length >= 4 && a.Id.ToString().StartsWith(id, StringComparison.Ordinal))))
        .ToList();
    if (change is [var pending] && approve)
    {
        var request = JsonSerializer.Deserialize<PoniExp.Core.Protocol.AdminRequest>(pending.Command!, WireProtocol.Json)!;
        Console.WriteLine($"Approving the docker exec change {request.Code}: poniexp admin {string.Join(" ", request.Args)}");
        return await RunSignedAdminAsync(api, [.. request.Args], request);
    }

    if (join is [var machine])
    {
        Console.WriteLine($"{(approve ? "Approving" : "Removing")} {machine.From}, join code {machine.Command}:");
        return await RunSignedAdminAsync(api, [approve ? "approve" : "remove", machine.Id.ToString("N")]); // a join's id is the machine's id
    }

    ShowApproval(await api.AnswerAsync(id, approve, always, reason, cancel.Token));
    return 0;
}

int Completion(string shell)
{
    Console.Write(PoniExp.Client.Completion.CompletionScripts.For(shell));
    return 0;
}

/// <summary>
/// `complete --line <text up to the cursor> [--bash]` or `complete -- <words>`: the shell scripts' helper. It prints one
/// candidate per line and never fails, since an error on Tab would garble the prompt.
/// </summary>
async Task<int> CompleteAsync(string[] rest)
{
    var dash = Array.IndexOf(rest, "--");
    var line = Option(dash < 0 ? rest : rest[..dash], "--line");
    var words = line is not null ? PoniExp.Client.Completion.CommandCompletion.SplitLine(line) : dash >= 0 ? rest[(dash + 1)..] : [string.Empty];
    using var quick = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
    quick.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        using var api = await LocalApiClient.FindAsync(ClientConfig.HomeDirectory, quick.Token);
        PoniExp.Client.Completion.ICompletionSource source = await api.IsRunningAsync(quick.Token)
            ? new PoniExp.Client.Completion.LocalApiCompletionSource(api) : new NoDaemonSource();
        var candidates = await PoniExp.Client.Completion.CommandCompletion.CompleteAsync(words, source, quick.Token);
        if (rest.Contains("--bash"))
        {
            candidates = PoniExp.Client.Completion.CommandCompletion.ForBash(words.Count == 0 ? string.Empty : words[^1], candidates);
        }

        Console.Write(string.Concat(candidates.Select(c => c + "\n")));
    }
    catch (Exception)
    {
        // nothing to offer: the shell falls back to file names
    }

    return 0;
}

async Task<int> RulesAsync()
{
    using var api = await DaemonAsync2();
    var rules = await api.ApprovalRulesAsync(cancel.Token);
    Console.WriteLine(rules.Count == 0 ? "no always-allow rules" : string.Join("\n", rules.Select(r =>
        $"{r.Id.ToString()[..8]}  {r.Agent}  {r.Tool ?? "(request)"}  {r.Subject ?? "(anything)"}  since {r.CreatedOn.ToLocalTime():yyyy-MM-dd}")));
    return 0;
}

async Task<int> RemoveRuleAsync(string id)
{
    using var api = await DaemonAsync2();
    await api.RemoveApprovalRuleAsync(id, cancel.Token);
    Console.WriteLine("removed");
    return 0;
}

async Task<int> RoomAsync(string[] rest)
{
    using var api = await DaemonAsync2();
    string As() => Option(rest, "--as") ?? throw new ArgumentException("say which agent you are: --as <agent>");
    var positional = new List<string>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i] is "--as" or "--invite" or "--since" or "--reply" or "--agent")
        {
            i++;
        }
        else if (rest[i] != "--json")
        {
            positional.Add(rest[i]);
        }
    }

    static void Show(LocalRoom r) =>
        Console.WriteLine($"{r.Name}  {r.Id.ToString()[..8]}{(r.Collapsed ? $" (collapsed: {r.CollapseReason})" : "")}  opened by {r.Creator} {r.OpenedOn.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                          $"    members: {(r.Members.Count == 0 ? "none" : string.Join(", ", r.Members))}" +
                          (r.Invited.Count > 0 ? $"\n    invited: {string.Join(", ", r.Invited.Select(i => i.Invitee))}" : ""));

    switch (positional)
    {
        case ["list"]:
            var rooms = await api.RoomsAsync(Option(rest, "--agent"), cancel.Token);
            if (rooms.Count == 0)
            {
                Console.WriteLine("no rooms");
            }

            rooms.ForEach(Show);
            return 0;
        case ["invites"]:
            var invites = await api.RoomInvitesAsync(Option(rest, "--agent"), cancel.Token);
            Console.WriteLine(invites.Count == 0 ? "no open invitations" : string.Join("\n", invites.Select(i =>
                $"{i.Room,-20} {i.Invitee}, invited by {i.InvitedBy}, until {i.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm}")));
            return 0;
        case ["create", var name]:
            Show(await api.CreateRoomAsync(new LocalRoomCreate(As(), name, Options(rest, "--invite")), cancel.Token));
            return 0;
        case ["invite", var room, .. var addresses] when addresses.Count > 0:
            Show(await api.InviteToRoomAsync(room, new LocalRoomInviteRequest(As(), addresses), cancel.Token));
            return 0;
        case ["join", var room]:
            Show(await api.JoinRoomAsync(room, As(), cancel.Token));
            return 0;
        case ["leave", var room]:
            Show(await api.LeaveRoomAsync(room, As(), cancel.Token));
            return 0;
        case ["read", var room]:
            var since = Option(rest, "--since") is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
            foreach (var m in await api.ReadRoomAsync(room, As(), since, ct: cancel.Token))
            {
                Console.WriteLine(rest.Contains("--json") ? JsonSerializer.Serialize(m, WireProtocol.Json) : Format(m));
            }

            return 0;
        case ["send", var room, var body]:
            var sent = await api.SendToRoomAsync(room, new LocalRoomSend(As(), body == "-" ? await Console.In.ReadToEndAsync() : body,
                Option(rest, "--reply") is { } r ? Guid.Parse(r) : null), cancel.Token);
            Console.WriteLine($"{sent.State}: {sent.Id} to {sent.To}");
            return 0;
        default:
            return Fail(Usage);
    }
}

async Task<int> FetchAsync(string agent, Guid id, Guid? blob)
{
    using var api = await DaemonAsync2();
    foreach (var a in await api.FetchAsync(agent, new LocalFileRequest(id, blob), cancel.Token))
    {
        Console.WriteLine($"{a.Name} ({FormatBytes(a.Size)}, {a.State}): {a.Path}");
    }

    return 0;
}

async Task<int> ReleaseAsync(string agent, Guid id, Guid? blob)
{
    using var api = await DaemonAsync2();
    foreach (var a in await api.ReleaseAsync(agent, new LocalFileRequest(id, blob), cancel.Token))
    {
        Console.WriteLine($"{a.Name}: {a.State}");
    }

    return 0;
}

async Task<int> BlobsAsync()
{
    using var api = await DaemonAsync2();
    var blobs = await api.BlobsAsync(cancel.Token);
    if (blobs.Count == 0)
    {
        Console.WriteLine("no files on the router");
    }

    foreach (var b in blobs)
    {
        Console.WriteLine($"{b.BlobId}  {WireProtocol.NameOf(b.State),-10} {FormatBytes(b.Bytes),9}  to {string.Join(", ", b.SharedWith)}" +
                          $"{(b.ReleasedBy.Count == 0 ? "" : $"  (fetched: {string.Join(", ", b.ReleasedBy)})")}  until {b.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm}");
    }

    return 0;
}

async Task<int> CancelBlobAsync(Guid blob)
{
    using var api = await DaemonAsync2();
    await api.CancelBlobAsync(blob, cancel.Token);
    Console.WriteLine($"cancelled {blob}: the router deleted it; recipients that hadn't fetched it are told");
    return 0;
}

async Task<int> AgentsAsync()
{
    using var api = await DaemonAsync2();
    foreach (var e in await api.AgentsAsync(cancel.Token))
    {
        Console.WriteLine($"{e.Name,-20} {e.State,-10} since {e.ChangedOn.ToLocalTime():yyyy-MM-dd HH:mm}");
    }

    return 0;
}

async Task<int> RetireAsync(string name)
{
    using var api = await DaemonAsync2();
    var e = await api.RetireAsync(name, cancel.Token);
    Console.WriteLine($"retired {e.Name}: its messages are now rejected");
    return 0;
}

async Task<int> DiagnosticsAsync()
{
    using var api = await DaemonAsync2();
    foreach (var d in await api.DiagnosticsAsync(cancel.Token))
    {
        Console.WriteLine($"{d.OccurredOn.ToLocalTime():yyyy-MM-dd HH:mm:ss} {d.Level,-7} {d.Category,-10} {d.Detail}");
    }

    return 0;
}

async Task<int> CapabilitiesAsync()
{
    using var api = await DaemonAsync2();
    var c = await api.CapabilitiesAsync(cancel.Token);
    Console.WriteLine(c.Summary);
    Console.WriteLine($"  os={c.Capabilities.Os} os-version={c.Capabilities.OsVersion} arch={c.Capabilities.Arch} cores={c.Capabilities.Cores} " +
                      $"memory={c.Capabilities.MemoryGb:0.#} gpus={c.Capabilities.Gpus.Count}");
    return 0;
}

async Task<int> McpAsync(string agent)
{
    using var api = await LocalApiClient.FindAsync(ClientConfig.HomeDirectory, cancel.Token);
    await PoniExp.Mcp.McpServer.RunAsync(api, agent, cancel.Token);
    return 0;
}

// ---- helpers ----

async Task<LocalApiClient> DaemonAsync2()
{
    var api = await LocalApiClient.FindAsync(ClientConfig.HomeDirectory, cancel.Token);
    if (!await api.IsRunningAsync(cancel.Token))
    {
        api.Dispose();
        throw new InvalidOperationException("the daemon isn't running; start it with 'poniexp-client service install' or 'poniexp-client daemon'");
    }

    return api;
}

/// <summary>A direct connection for join and doctor before a daemon runs. Its scratch database keeps anything delivered unconfirmed, so the daemon still gets it.</summary>
async Task<RelayClient> ConnectDirectAsync(bool probe)
{
    var config = ClientConfig.Load() ?? throw new InvalidOperationException("run 'poniexp-client init <router-url>' first");
    var settings = new ConfigurationBuilder()
        .AddJsonFile(ClientConfig.SettingsPath, optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("PONIEXP_")
        .Build();
    var keys = KeyRing.LoadOrCreate(ClientConfig.HomeDirectory);
    var scratch = Path.Combine(Path.GetTempPath(), $"poniexp-probe-{Guid.NewGuid():N}.db");
    var options = config.ToOptions();
    var client = new RelayClient(keys, new RelayClientOptions
    {
        RouterUrl = options.RouterUrl,
        PinnedCertificateSha256 = options.PinnedCertificateSha256,
        ConfirmMessages = !probe,
    }, new ClientStore(scratch), MessagingSettings.Monitor(settings));
    await client.ConnectAsync(cancel.Token);
    return client;
}

static string Format(LocalMessage m) =>
    $"[{m.CreatedOn.ToLocalTime():HH:mm:ss}] {m.Id.ToString()[..8]} {m.From} -> {m.To}" +
    (m.ReplyTo is { } re ? $" (re {re.ToString()[..8]}{(m.Status is { } s ? $", {s}" : string.Empty)})" : string.Empty) +
    (m.Subject is null ? string.Empty : $" [{m.Subject}]") + $" <{m.State}>: {m.Body}" +
    string.Concat((m.Attachments ?? []).Select(a => $"\n    + {a.Name} ({FormatBytes(a.Size)}, {a.State}{(a.Path is null ? "" : $": {a.Path}")}" +
                                                   $"{(a.Detail is null ? "" : $"; {a.Detail}")})"));

static string FormatBytes(long bytes) =>
    bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.#} GB" : bytes >= 1_000_000 ? $"{bytes / 1e6:0.#} MB" : bytes >= 1_000 ? $"{bytes / 1e3:0.#} KB" : $"{bytes} B";

static string FormatOffset(TimeSpan offset) =>
    offset.Duration() < TimeSpan.FromSeconds(1) ? "under a second" : $"{offset.Duration().TotalSeconds:0.#} s {(offset > TimeSpan.Zero ? "behind" : "ahead of")}";

static void AddMeta(Dictionary<string, string> metadata, string pair)
{
    var eq = pair.IndexOf('=', StringComparison.Ordinal);
    if (eq <= 0)
    {
        throw new ArgumentException($"--meta takes key=value, not '{pair}'.");
    }

    metadata[pair[..eq]] = pair[(eq + 1)..];
}

static string AgentOf(string[] rest) => Option(rest, "--agent") ?? Address.DefaultAgent;

static TimeSpan ParseDuration(string text)
{
    if (text.Length > 1 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
    {
        switch (text[^1])
        {
            case 's': return TimeSpan.FromSeconds(amount);
            case 'm': return TimeSpan.FromMinutes(amount);
            case 'h': return TimeSpan.FromHours(amount);
            case 'd': return TimeSpan.FromDays(amount);
        }
    }

    return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span)
        ? span
        : throw new ArgumentException($"--expires takes 30s, 10m, 2h, 3d or a TimeSpan, not '{text}'.");
}

static IReadOnlyList<string> Options(string[] rest, string name) =>
    [.. rest.Select((value, i) => (value, i)).Where(p => p.value == name && p.i + 1 < rest.Length).Select(p => rest[p.i + 1])];

static string? Option(string[] rest, string name)
{
    var i = Array.IndexOf(rest, name);
    return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
}

static string[] TakeHome(string[] args)
{
    var i = Array.IndexOf(args, "--home");
    if (i < 0 || i + 1 >= args.Length)
    {
        return args;
    }

    Environment.SetEnvironmentVariable("PONIEXP_HOME", Path.GetFullPath(args[i + 1]));
    return [.. args[..i], .. args[(i + 2)..]];
}

static bool IsAdmin()
{
    if (OperatingSystem.IsWindows())
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    return Environment.UserName == "root";
}

static async Task<(int Code, string Output)> RunAsync(string[] command, bool quiet)
{
    using var process = Process.Start(new ProcessStartInfo(command[0], command[1..])
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    }) ?? throw new InvalidOperationException($"can't run {command[0]}");
    var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (!quiet && output.Length > 0)
    {
        Console.Write(output);
    }

    return (process.ExitCode, output);
}

static void Copy(string from, string to)
{
    if (File.Exists(from))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
        return;
    }

    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static int Fail(string message)
{
    Console.Error.WriteLine(message.TrimEnd());
    return 2;
}

/// <summary>With the daemon stopped, commands and fixed values still complete; nothing that needs the daemon does.</summary>
sealed class NoDaemonSource : PoniExp.Client.Completion.ICompletionSource
{
    public Task<IReadOnlyList<string>> ListAsync(PoniExp.Client.Completion.CompletionKind kind, string? agent, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
