using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Secrets;

const string Usage = """
    poniexp-client init <router-url> [--pin <sha256>]   where the router is; creates this machine's key
    poniexp-client id                                   this machine's id and join code
    poniexp-client join <name> [--notes <text>]         ask to join; waits for approval
    poniexp-client status                               membership, who's online, and message metrics
    poniexp-client send --to <machine[/endpoint]> [--to ...] [options] <body|->
                                                        sign and send; one separate message per --to
        --from <endpoint>        sending endpoint (default: "default")
        --subject <text>         a title for conversation lists
        --reply-to <id>          the message this answers (one --to only)
        --conversation <id>      continue a conversation (one --to only)
        --operation <id>         the same id across retries of one action (one --to only)
        --status <progress|done|failed>   for replies: the outcome of the work
        --expires <30s|10m|2h|3d>         how long it lives (default and maximum from settings)
        --content-type <type>    how to read the body (default: text/plain)
        --meta <key=value>       metadata; repeatable
        "-" as the body reads stdin
    poniexp-client listen [--json]                      stay connected and print messages as they arrive

    Settings: ~/.poniexp/settings.json, e.g. { "Messaging": { "DefaultExpiry": "1.00:00:00" } }
    """;

var keys = KeyRing.LoadOrCreate(ClientConfig.HomeDirectory); // also creates the home directory and, first time, both keys
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
        ["join", var name, .. var rest] => await JoinAsync(name, Option(rest, "--notes")),
        ["status"] => await StatusAsync(),
        ["send", .. var rest] => await SendAsync(rest),
        ["listen", .. var rest] => await ListenAsync(rest.Contains("--json")),
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
catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or HttpRequestException
                               or Microsoft.AspNetCore.SignalR.HubException or System.Net.WebSockets.WebSocketException)
{
    var message = e.InnerException?.InnerException is System.Security.Authentication.AuthenticationException
        ? "the router's TLS certificate doesn't match the pinned SHA-256 (check 'poniexp-client init ... --pin')"
        : e is ArgumentException { ParamName: { } param } argument
            ? argument.Message.Replace($" (Parameter '{param}')", string.Empty, StringComparison.Ordinal)
            : e.Message;
    return Fail("error: " + message);
}

int Init(string url, string? pin)
{
    new ClientConfig(url, pin?.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant()).Save();
    Console.WriteLine($"router: {url}{(pin is null ? " (no pin: normal TLS validation)" : $" (pinned {pin[..16]}...)")}");
    return Id();
}

int Id()
{
    Console.WriteLine($"machine id: {keys.MachineId}");
    Console.WriteLine($"join code:  {KeyFingerprint.JoinCodeOf(keys.PublicKey)}");
    Console.WriteLine($"keys:       {SecretStores.ForHome(ClientConfig.HomeDirectory).Description}");
    return 0;
}

async Task<int> JoinAsync(string name, string? notes)
{
    await using var client = await ConnectAsync();
    if (client.Status == MembershipStatus.Approved)
    {
        Console.WriteLine($"already approved as {client.Name}");
        return 0;
    }

    var approved = new TaskCompletionSource<MembershipStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.MembershipChanged += status => approved.TrySetResult(status);
    var status = await client.RequestJoinAsync(name, MachineDescription.Detect(notes), cancel.Token);
    if (status is MembershipStatus.Rejected or MembershipStatus.Approved)
    {
        Console.WriteLine($"this key was already {status.ToString().ToLowerInvariant()}");
        return status == MembershipStatus.Approved ? 0 : 1;
    }

    Console.WriteLine($"asked to join as '{name}'. On the router, check this join code and approve:");
    Console.WriteLine($"    {client.JoinCode}");
    Console.WriteLine($"    docker exec poniexp-server poniexp admin approve {client.MachineId[..8]}");
    Console.WriteLine("waiting (Ctrl+C to stop; the request stays pending)...");
    var result = await approved.Task.WaitAsync(cancel.Token);
    Console.WriteLine(result == MembershipStatus.Approved ? "approved" : $"not approved: {result}");
    return result == MembershipStatus.Approved ? 0 : 1;
}

async Task<int> StatusAsync()
{
    await using var client = await ConnectAsync();
    Console.WriteLine($"{client.Status}{(client.Name is null ? string.Empty : $" as {client.Name}")} ({client.MachineId[..8]})");
    foreach (var m in client.Machines.OrderBy(m => m.Name))
    {
        var seen = m.Online ? "online" : m.LastSeenOn is { } on ? $"last seen {on.ToLocalTime():yyyy-MM-dd HH:mm}" : "never seen";
        Console.WriteLine($"  {m.Name,-16} {m.Id[..8]}  {seen,-28} {m.Description.OperatingSystem}");
    }

    var metrics = await client.Store.GetMetricsAsync();
    if (metrics.Count > 0)
    {
        Console.WriteLine("metrics:");
        var names = client.Machines.ToDictionary(m => m.Id, m => m.Name);
        foreach (var metric in metrics)
        {
            Console.WriteLine($"  {names.GetValueOrDefault(metric.PeerMachine, metric.PeerMachine[..8]),-16} {metric.Name,-16} " +
                              $"{metric.Count,6}  last {metric.LastOn.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
    }

    return client.Status == MembershipStatus.Approved ? 0 : 1;
}

async Task<int> SendAsync(string[] rest)
{
    var to = new List<string>();
    var metadata = new Dictionary<string, string>();
    var message = new OutgoingMessage(string.Empty);
    string? body = null;
    for (var i = 0; i < rest.Length; i++)
    {
        string Next() => i + 1 < rest.Length ? rest[++i] : throw new ArgumentException($"{rest[i]} needs a value.");
        switch (rest[i])
        {
            case "--to": to.Add(Next()); break;
            case "--from": message = message with { FromEndpoint = Next() }; break;
            case "--subject": message = message with { Subject = Next() }; break;
            case "--reply-to": message = message with { ReplyTo = Guid.Parse(Next()) }; break;
            case "--conversation": message = message with { ConversationId = Guid.Parse(Next()) }; break;
            case "--operation": message = message with { OperationId = Guid.Parse(Next()) }; break;
            case "--status": message = message with { Status = ParseStatus(Next()) }; break;
            case "--expires": message = message with { ExpiresIn = ParseDuration(Next()) }; break;
            case "--content-type": message = message with { ContentType = Next() }; break;
            case "--meta":
                var pair = Next();
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    throw new ArgumentException($"--meta takes key=value, not '{pair}'.");
                }

                metadata[pair[..eq]] = pair[(eq + 1)..];
                break;
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

    message = message with
    {
        Body = body == "-" ? await Console.In.ReadToEndAsync() : body,
        Metadata = metadata.Count == 0 ? null : metadata,
    };
    await using var client = await ConnectApprovedAsync();
    foreach (var sent in await client.SendAsync(to, message, cancel.Token))
    {
        var name = client.Machines.FirstOrDefault(m => m.Id == sent.ToMachine)?.Name ?? sent.ToMachine[..8];
        Console.WriteLine($"{sent.State.ToString().ToLowerInvariant()}: {sent.Id} to {name}/{sent.ToEndpoint} " +
                          $"(#{sent.Sequence}, expires {sent.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm})");
    }

    return 0;
}

async Task<int> ListenAsync(bool json)
{
    await using var client = await ConnectApprovedAsync();
    client.MessageReceived += message =>
    {
        var e = message.Envelope;
        var c = message.Content;
        Console.WriteLine(json
            ? JsonSerializer.Serialize(new
            {
                e.Id, from = message.From.ToString(), e.FromMachine, to = c.ToEndpoint, c.ConversationId, c.ReplyTo, c.OperationId,
                c.Status, c.Subject, c.Type, c.ContentType, c.Metadata, c.Body, c.CreatedOn, e.ExpiresOn, message.RelayedOn,
            }, WireProtocol.Json)
            : $"[{c.CreatedOn.ToLocalTime():HH:mm:ss}] {message.From} -> {c.ToEndpoint}" +
              (c.ReplyTo is { } re ? $" (re {re.ToString()[..8]}{(c.Status is { } s ? $", {WireProtocol.NameOf(s)}" : string.Empty)})" : string.Empty) +
              (c.Subject is null ? string.Empty : $" [{c.Subject}]") + $": {c.Body}");
        return Task.CompletedTask;
    };
    client.PresenceChanged += p => Console.Error.WriteLine($"* {p.Name} is {(p.Online ? "online" : "offline")}");
    client.ConnectionChanged += (up, error) => Console.Error.WriteLine(up ? "* connected" : $"* connection lost: {error?.Message}; retrying");
    client.MessageRejected += (e, why) => Console.Error.WriteLine($"! dropped envelope {e?.Id}: {why}");
    Console.Error.WriteLine($"* listening as {client.Name}; Ctrl+C to stop");
    await Task.Delay(Timeout.Infinite, cancel.Token);
    return 0;
}

async Task<RelayClient> ConnectAsync()
{
    var config = ClientConfig.Load() ?? throw new InvalidOperationException("run 'poniexp-client init <router-url>' first");
    var settings = new ConfigurationBuilder()
        .AddJsonFile(ClientConfig.SettingsPath, optional: true, reloadOnChange: true)
        .AddEnvironmentVariables("PONIEXP_")
        .Build();
    var client = new RelayClient(keys, config.ToOptions(), new ClientStore(ClientConfig.DatabasePath), MessagingSettings.Monitor(settings));
    await client.ConnectAsync(cancel.Token);
    return client;
}

async Task<RelayClient> ConnectApprovedAsync()
{
    var client = await ConnectAsync();
    if (client.Status != MembershipStatus.Approved)
    {
        await client.DisposeAsync();
        throw new InvalidOperationException($"this machine is {client.Status.ToString().ToLowerInvariant()}; run 'poniexp-client join <name>'");
    }

    return client;
}

static ReplyStatus ParseStatus(string text)
{
    foreach (var status in Enum.GetValues<ReplyStatus>())
    {
        if (WireProtocol.NameOf(status) == text)
        {
            return status;
        }
    }

    throw new ArgumentException("--status is progress, done or failed.");
}

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

static int Fail(string message)
{
    Console.Error.WriteLine(message.TrimEnd());
    return 2;
}
