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
      join <name> [--notes <text>]         ask to join; waits for approval (through the daemon if it runs)
      doctor                               check the router, certificate pin, clock, membership and daemon

    Messaging (through the daemon)
      status                               this machine, its endpoints, and message metrics
      machines                             the other machines and their endpoints
      send --to <machine[/endpoint]> [--to ...] [options] <body|->
                                           one separate message per --to
          --from <endpoint>        sending endpoint (default: "default")
          --subject <text>         a title for conversation lists
          --conversation <id>      continue a conversation (one --to only)
          --operation <id>         the same id across retries of one action (one --to only)
          --expires <30s|10m|2h|3d>         how long it lives (default and maximum from settings)
          --content-type <type>    how to read the body (default: text/plain)
          --meta <key=value>       metadata; repeatable
          --attach <path>          a file, uploaded once for all recipients; repeatable
      inbox [--endpoint <name>] [--json]   unacknowledged messages for an endpoint (default: "default")
      ack <id> [--endpoint <name>]         acknowledge a message (tell its sender you've taken it on)
      reply <id> [--endpoint <name>] [--status progress|done|failed] [--meta k=v] [--attach <path>] <body|->
                                           reply in its conversation (acknowledges it too)
      message <id> [--endpoint <name>]     one message and its delivery history
      conversation <id> [--endpoint <name>] [--json]
      fetch <id> [--endpoint <name>] [--blob <id>]
                                           download a message's files (verified) and print where they are
      release <id> [--endpoint <name>] [--blob <id>]
                                           done with them: the copy goes once every endpoint has released it
      blobs | cancel <blob-id>             files this machine uploaded; cancel one (the router deletes it)
      listen [--endpoint <name>] [--json] [--ack] [--auto-fetch]
                                           be the endpoint: print messages and receipts as they arrive
      conversations [--endpoint <name>]    an endpoint's conversations, most recent first
      log [filters] [--json | --export <path|->] [--bodies]
                                           the machine's message log, oldest first
          --conversation <id> --operation <id> --peer <machine> --endpoint <name>
          --since <2h|3d|date> --limit <n> --automatic (receipts, announcements too)
          --bodies                 decrypt bodies into the output (they're then in the clear)
      endpoint list | endpoint retire <name>
                                           the endpoint registry; a retired endpoint's messages are rejected
      diagnostics                          recent connection, signature and delivery problems
      mcp --endpoint <name>                an MCP server (stdio) for an agent, as that endpoint

    "-" as a body reads stdin. Settings: <home>/settings.json, e.g.
      { "Messaging": { "DefaultExpiry": "1.00:00:00" }, "Daemon": { "EndpointTimeout": "00:01:00" } }
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
        ["inbox", .. var rest] => await InboxAsync(EndpointOf(rest), rest.Contains("--json")),
        ["ack", var id, .. var rest] => await AckAsync(EndpointOf(rest), Guid.Parse(id)),
        ["reply", var id, .. var rest] => await ReplyAsync(Guid.Parse(id), rest),
        ["message", var id, .. var rest] => await MessageAsync(EndpointOf(rest), Guid.Parse(id)),
        ["conversation", var id, .. var rest] => await ConversationAsync(EndpointOf(rest), Guid.Parse(id), rest.Contains("--json")),
        ["listen", .. var rest] => await ListenAsync(EndpointOf(rest), rest.Contains("--json"), rest.Contains("--ack"), rest.Contains("--auto-fetch")),
        ["fetch", var id, .. var rest] => await FetchAsync(EndpointOf(rest), Guid.Parse(id), Option(rest, "--blob") is { } b ? Guid.Parse(b) : null),
        ["release", var id, .. var rest] => await ReleaseAsync(EndpointOf(rest), Guid.Parse(id), Option(rest, "--blob") is { } b ? Guid.Parse(b) : null),
        ["blobs"] => await BlobsAsync(),
        ["cancel", var blob] => await CancelBlobAsync(Guid.Parse(blob)),
        ["conversations", .. var rest] => await ConversationsAsync(EndpointOf(rest)),
        ["log", .. var rest] => await LogAsync(rest),
        ["endpoint", "list"] => await EndpointsAsync(),
        ["endpoint", "retire", var name] => await RetireAsync(name),
        ["diagnostics"] => await DiagnosticsAsync(),
        ["mcp", .. var rest] => await McpAsync(Option(rest, "--endpoint") ?? throw new ArgumentException("mcp needs --endpoint <name>")),
        ["service", "install", .. var rest] => await ServiceInstallAsync(rest.Contains("--system") ? ServiceMode.System : ServiceMode.User, rest.Contains("--dry-run")),
        ["service", "uninstall", .. var rest] => await ServiceUninstallAsync(rest.Contains("--system") ? ServiceMode.System : ServiceMode.User, rest.Contains("--dry-run")),
        ["service", "status"] => await ServiceStatusAsync(),
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
catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or HttpRequestException or LocalApiException
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
    var context = new DaemonContext(keys, home, config.ToOptions(), new ClientStore(ClientConfig.DatabasePath), LocalAddress.ForHome(home));
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

        PrintJoinInstructions(name, result.JoinCode, result.MachineId);
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

    PrintJoinInstructions(name, client.JoinCode, client.MachineId);
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
    Console.WriteLine("endpoints:");
    foreach (var e in s.Endpoints)
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
        Console.WriteLine($"{m.Name,-16} {m.Id[..8]}  {seen,-28} {m.OperatingSystem}");
        foreach (var e in m.Endpoints)
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
    string from = Address.DefaultEndpoint;
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

async Task<int> InboxAsync(string endpoint, bool json)
{
    using var api = await DaemonAsync2();
    var messages = await api.InboxAsync(endpoint, cancel.Token);
    foreach (var m in messages)
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(m, WireProtocol.Json) : Format(m));
    }

    if (!json && messages.Count == 0)
    {
        Console.Error.WriteLine($"* nothing waiting for {endpoint}");
    }

    return 0;
}

async Task<int> AckAsync(string endpoint, Guid id)
{
    using var api = await DaemonAsync2();
    var m = await api.AckAsync(endpoint, id, cancel.Token);
    Console.WriteLine($"acknowledged {m.Id} from {m.From}");
    return 0;
}

async Task<int> ReplyAsync(Guid id, string[] rest)
{
    var metadata = new Dictionary<string, string>();
    var files = new List<string>();
    string endpoint = Address.DefaultEndpoint;
    string? status = null, body = null;
    for (var i = 0; i < rest.Length; i++)
    {
        string Next() => i + 1 < rest.Length ? rest[++i] : throw new ArgumentException($"{rest[i]} needs a value.");
        switch (rest[i])
        {
            case "--endpoint": endpoint = Next(); break;
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
    var sent = await api.ReplyAsync(endpoint, new LocalReplyRequest(id, body == "-" ? await Console.In.ReadToEndAsync() : body, status,
        Metadata: metadata.Count == 0 ? null : metadata, Attachments: files.Count == 0 ? null : files), cancel.Token);
    Console.WriteLine($"{sent.State}: {sent.Id} to {sent.To} (#{sent.Sequence})");
    return 0;
}

async Task<int> MessageAsync(string endpoint, Guid id)
{
    using var api = await DaemonAsync2();
    var detail = await api.MessageAsync(endpoint, id, cancel.Token);
    Console.WriteLine(Format(detail.Message));
    foreach (var e in detail.Events)
    {
        Console.WriteLine($"  {e.OccurredOn.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {e.Event}{(e.Detail is null ? string.Empty : $" ({e.Detail})")}");
    }

    return 0;
}

async Task<int> ConversationAsync(string endpoint, Guid id, bool json)
{
    using var api = await DaemonAsync2();
    foreach (var m in await api.ConversationAsync(endpoint, id, cancel.Token))
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(m, WireProtocol.Json) : Format(m));
    }

    return 0;
}

async Task<int> ListenAsync(string endpoint, bool json, bool ack, bool autoFetch)
{
    using var api = await DaemonAsync2();
    await using var connection = new EndpointConnection(api, endpoint, async e =>
    {
        if (e.Message is { } m)
        {
            Console.WriteLine(json ? JsonSerializer.Serialize(m, WireProtocol.Json) : Format(m));
            if (autoFetch && m.Attachments is { Count: > 0 })
            {
                try
                {
                    foreach (var a in await api.FetchAsync(endpoint, new LocalFileRequest(m.Id), cancel.Token))
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
                await api.AckAsync(endpoint, m.Id, cancel.Token);
            }
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
    Console.Error.WriteLine($"* listening as {endpoint}; Ctrl+C to stop");
    try
    {
        await Task.Delay(Timeout.Infinite, cancel.Token);
    }
    catch (OperationCanceledException)
    {
    }

    return 0;
}

async Task<int> ConversationsAsync(string endpoint)
{
    using var api = await DaemonAsync2();
    foreach (var c in await api.ConversationsAsync(endpoint, cancel.Token))
    {
        Console.WriteLine($"{c.Id.ToString()[..8]}  {c.LastActivityOn.ToLocalTime():yyyy-MM-dd HH:mm}  {c.State,-8}  {c.Peer,-28}  {c.Subject}");
    }

    return 0;
}

async Task<int> LogAsync(string[] rest)
{
    Guid? conversation = null, operation = null;
    string? peer = null, endpoint = null, export = null;
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
            case "--endpoint": endpoint = Next(); break;
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
    var entries = await api.LogAsync(new LocalLogQuery(conversation, operation, peer, endpoint, since, limit, automatic, bodies), cancel.Token);
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

async Task<int> FetchAsync(string endpoint, Guid id, Guid? blob)
{
    using var api = await DaemonAsync2();
    foreach (var a in await api.FetchAsync(endpoint, new LocalFileRequest(id, blob), cancel.Token))
    {
        Console.WriteLine($"{a.Name} ({FormatBytes(a.Size)}, {a.State}): {a.Path}");
    }

    return 0;
}

async Task<int> ReleaseAsync(string endpoint, Guid id, Guid? blob)
{
    using var api = await DaemonAsync2();
    foreach (var a in await api.ReleaseAsync(endpoint, new LocalFileRequest(id, blob), cancel.Token))
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

async Task<int> EndpointsAsync()
{
    using var api = await DaemonAsync2();
    foreach (var e in await api.EndpointsAsync(cancel.Token))
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

async Task<int> McpAsync(string endpoint)
{
    using var api = await LocalApiClient.FindAsync(ClientConfig.HomeDirectory, cancel.Token);
    await PoniExp.Mcp.McpServer.RunAsync(api, endpoint, cancel.Token);
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

static string EndpointOf(string[] rest) => Option(rest, "--endpoint") ?? Address.DefaultEndpoint;

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
