using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server;
using PoniExp.Server.Admin;
using PoniExp.Server.Machines;

namespace PoniExp.Tests;

/// <summary>A real router on a random loopback port, with its own data directory, and clients with their own databases.</summary>
public sealed class RouterFixture : IAsyncDisposable
{
    private readonly List<RelayClient> _clients = [];

    private RouterFixture(WebApplication app, string dataDirectory)
    {
        App = app;
        DataDirectory = dataDirectory;
    }

    public WebApplication App { get; }

    public string DataDirectory { get; }

    /// <summary>Leave the data directory on disposal, for a router started on it again (a restart).</summary>
    public bool KeepData { get; set; }

    public Uri Url => new(RouterHost.Addresses(App).First());

    public AdminService Admin => App.Services.GetRequiredService<AdminService>();

    public MessageQueue Queue => App.Services.GetRequiredService<MessageQueue>();

    public PoniExp.Server.Hubs.ConnectionGate Gate => App.Services.GetRequiredService<PoniExp.Server.Hubs.ConnectionGate>();

    public PoniExp.Server.Blobs.BlobRegistry Blobs => App.Services.GetRequiredService<PoniExp.Server.Blobs.BlobRegistry>();

    public PoniExp.Server.Jobs.JobBoard Jobs => App.Services.GetRequiredService<PoniExp.Server.Jobs.JobBoard>();

    public PoniExp.Server.Owner.OwnerDesk Desk => App.Services.GetRequiredService<PoniExp.Server.Owner.OwnerDesk>();

    public PoniExp.Server.Rooms.RoomRegistry Rooms => App.Services.GetRequiredService<PoniExp.Server.Rooms.RoomRegistry>();

    public PoniExp.Server.Hubs.PresenceTracker Presence => App.Services.GetRequiredService<PoniExp.Server.Hubs.PresenceTracker>();

    /// <summary>
    /// A free port outside the OS's ephemeral range (Linux 32768+, Windows 49152+), for a router a test restarts on the
    /// same port: no parallel test binding port 0 can be handed it while it's down.
    /// </summary>
    public static int RestartablePort()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 30000);
            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException) when (attempt < 50)
            {
            }
        }
    }

    /// <param name="settings">Router settings ("PoniExp:MaxExpiry", ...), changeable later with <see cref="SetSetting"/>.</param>
    public static async Task<RouterFixture> StartAsync(bool useHttps = false, string? dataDirectory = null,
        IDictionary<string, string?>? settings = null, int port = 0, int? adminPort = null, int? dashboardPort = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        var app = RouterHost.Build(["--Logging:LogLevel:Default=None"], o =>
        {
            o.DataDirectory = dataDirectory;
            o.BindAddress = "127.0.0.1";
            o.Port = port;
            o.AdminPort = adminPort; // on for remote admin (it runs the admin commands through it)
            o.UseHttps = useHttps;
            o.DashboardPort = dashboardPort;
            o.DashboardBindAddress = "127.0.0.1";
        }, configuration => configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>()));
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new RouterFixture(app, dataDirectory);
    }

    /// <summary>Changes a router setting while it runs, as an edited config file would.</summary>
    public void SetSetting(string key, string value) => TestSettings.Set((IConfigurationRoot)App.Configuration, key, value);

    public string StorePath(MachineKeys keys) => Path.Combine(DataDirectory + "-clients", keys.MachineId + ".db");

    /// <summary>A client for <paramref name="keys"/>. Its database is kept per machine, so a new client is a restart.</summary>
    public RelayClient Client(MachineKeys keys, string? pin = null, IConfiguration? settings = null, bool confirm = true)
    {
        var client = new RelayClient(keys, new RelayClientOptions
        {
            RouterUrl = Url,
            PinnedCertificateSha256 = pin,
            ReconnectDelays = [TimeSpan.FromMilliseconds(100)],
            ConfirmMessages = confirm,
        }, new ClientStore(StorePath(keys)), MessagingSettings.Monitor(settings ?? TestSettings.Create()));
        _clients.Add(client);
        return client;
    }

    /// <summary>A connected, approved machine.</summary>
    public async Task<(RelayClient Client, MachineKeys Keys)> MemberAsync(string name, IConfiguration? settings = null)
    {
        var identity = MachineKeys.Create();
        var client = Client(identity, settings: settings);
        await client.ConnectAsync();
        var approved = Wait.For<MembershipStatus>(h => client.MembershipChanged += h);
        await client.RequestJoinAsync(name, new MachineDescription("TestOS 1", "x64", name + "-host"));
        await Admin.ApproveAsync(identity.MachineId);
        Assert.Equal(MembershipStatus.Approved, await approved);
        await Eventually(() => client.Machines.Any(m => m.Id == identity.MachineId));
        return (client, identity);
    }

    public static async Task Eventually(Func<bool> condition) => await EventuallyAsync(() => Task.FromResult(condition()));

    /// <summary>Waits until <paramref name="value"/> gives something other than null.</summary>
    public static async Task<T> EventuallyValueAsync<T>(Func<Task<T?>> value) where T : class
    {
        T? found = null;
        await EventuallyAsync(async () => (found = await value()) is not null);
        return found!;
    }

    public static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
            {
                Assert.Fail("condition not met within 10s");
            }

            await Task.Delay(25);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        await App.StopAsync();
        await App.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var directory in KeepData ? [] : new[] { DataDirectory, DataDirectory + "-clients" })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

public static class TestSettings
{
    public static IConfigurationRoot Create(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();

    /// <summary>Changes a setting in the in-memory source and reloads, which is what IOptionsMonitor listens for.</summary>
    public static void Set(IConfigurationRoot configuration, string key, string value)
    {
        configuration.Providers.OfType<MemoryConfigurationProvider>().Last().Set(key, value);
        configuration.Reload();
    }
}

public static class Wait
{
    public static Task<T> For<T>(Action<Action<T>> subscribe)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(value => source.TrySetResult(value));
        return source.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

/// <summary>Envelopes built by hand, for playing a misbehaving client or router.</summary>
public static class Envelopes
{
    public static MessageContent Content(DateTimeOffset createdOn, string body = "x") =>
        new("default", "default", Guid.NewGuid(), null, Guid.NewGuid(), null, null, MessageType.Message, "text/plain",
            new Dictionary<string, string>(), body, createdOn);

    public static Envelope Sign(MachineKeys from, MachineKeys to, DateTimeOffset createdOn, DateTimeOffset expiresOn,
        string body = "x", long sequence = 1) =>
        Signatures.SealEnvelope(from, Guid.NewGuid(), to.MachineId, to.EncryptionPublicKey, sequence, expiresOn, Content(createdOn, body));

    /// <summary>The envelope with its fields changed and signed again by <paramref name="signer"/>, as a forger would.</summary>
    public static Envelope Resign(Envelope envelope, MachineKeys signer)
    {
        var unsigned = envelope with { FromMachine = signer.MachineId, Signature = string.Empty };
        return unsigned with { Signature = signer.Sign(Signatures.EnvelopePayload(unsigned)) };
    }
}
