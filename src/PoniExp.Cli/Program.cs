using System.Text.Json;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

const string Usage = """
    poniexp-client init <router-url> [--pin <sha256>]   where the router is; creates this machine's key
    poniexp-client id                                   this machine's id and join code
    poniexp-client join <name> [--notes <text>]         ask to join; waits for approval
    poniexp-client status                               membership, and which machines are online
    poniexp-client send <machine> <kind> <body|->       sign and send a message ("-" reads stdin)
    poniexp-client listen [--json]                      stay connected and print messages as they arrive
    """;

var identity = MachineIdentity.LoadOrCreate(ClientConfig.KeyPath);
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
        ["send", var to, var kind, var body] => await SendAsync(to, kind, body == "-" ? await Console.In.ReadToEndAsync() : body),
        ["listen", .. var rest] => await ListenAsync(rest.Contains("--json")),
        _ => Fail(Usage),
    };
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception e) when (e is InvalidOperationException or HttpRequestException or Microsoft.AspNetCore.SignalR.HubException
                               or System.Net.WebSockets.WebSocketException)
{
    var message = e.InnerException?.InnerException is System.Security.Authentication.AuthenticationException
        ? "the router's TLS certificate doesn't match the pinned SHA-256 (check 'poniexp-client init ... --pin')"
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
    Console.WriteLine($"machine id: {identity.MachineId}");
    Console.WriteLine($"join code:  {KeyFingerprint.JoinCodeOf(identity.PublicKey)}");
    Console.WriteLine($"key:        {ClientConfig.KeyPath}");
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
        var seen = m.Online ? "online" : m.LastSeen is { } at ? $"last seen {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "never seen";
        Console.WriteLine($"  {m.Name,-16} {m.Id[..8]}  {seen,-28} {m.Description.OperatingSystem}");
    }

    return client.Status == MembershipStatus.Approved ? 0 : 1;
}

async Task<int> SendAsync(string to, string kind, string body)
{
    await using var client = await ConnectApprovedAsync();
    var result = await client.SendAsync(to, kind, body, cancel.Token);
    Console.WriteLine($"{result.State.ToString().ToLowerInvariant()}: {result.Id}");
    return 0;
}

async Task<int> ListenAsync(bool json)
{
    await using var client = await ConnectApprovedAsync();
    client.MessageReceived += message =>
    {
        var e = message.Envelope;
        Console.WriteLine(json
            ? JsonSerializer.Serialize(new { e.Id, from = message.FromName, e.From, e.Kind, e.Body, e.CreatedAt })
            : $"[{e.CreatedAt.ToLocalTime():HH:mm:ss}] {message.FromName} {e.Kind}: {e.Body}");
        return Task.CompletedTask;
    };
    client.PresenceChanged += p => Console.Error.WriteLine($"* {p.Name} is {(p.Online ? "online" : "offline")}");
    client.ConnectionChanged += (up, error) => Console.Error.WriteLine(up ? "* connected" : $"* connection lost: {error?.Message}; retrying");
    client.MessageRejected += (e, why) => Console.Error.WriteLine($"! dropped envelope {e.Id}: {why}");
    Console.Error.WriteLine($"* listening as {client.Name}; Ctrl+C to stop");
    await Task.Delay(Timeout.Infinite, cancel.Token);
    return 0;
}

async Task<RelayClient> ConnectAsync()
{
    var config = ClientConfig.Load() ?? throw new InvalidOperationException("run 'poniexp-client init <router-url>' first");
    var client = new RelayClient(identity, config.ToOptions());
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
