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

    public Uri Url => new(RouterHost.Addresses(App).First());

    public AdminService Admin => App.Services.GetRequiredService<AdminService>();

    public MessageQueue Queue => App.Services.GetRequiredService<MessageQueue>();

    /// <param name="settings">Router settings ("PoniExp:MaxExpiry", ...), changeable later with <see cref="SetSetting"/>.</param>
    public static async Task<RouterFixture> StartAsync(bool useHttps = false, string? dataDirectory = null,
        IDictionary<string, string?>? settings = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        var app = RouterHost.Build(["--Logging:LogLevel:Default=None"], o =>
        {
            o.DataDirectory = dataDirectory;
            o.BindAddress = "127.0.0.1";
            o.Port = 0;
            o.AdminPort = null;
            o.UseHttps = useHttps;
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

    public string StorePath(MachineIdentity identity) => Path.Combine(DataDirectory + "-clients", identity.MachineId + ".db");

    /// <summary>A client for <paramref name="identity"/>. Its database is kept per identity, so a new client is a restart.</summary>
    public RelayClient Client(MachineIdentity identity, string? pin = null, IConfiguration? settings = null, bool confirm = true)
    {
        var client = new RelayClient(identity, new RelayClientOptions
        {
            RouterUrl = Url,
            PinnedCertificateSha256 = pin,
            ReconnectDelays = [TimeSpan.FromMilliseconds(100)],
            ConfirmMessages = confirm,
        }, new ClientStore(StorePath(identity)), MessagingSettings.Monitor(settings ?? TestSettings.Create()));
        _clients.Add(client);
        return client;
    }

    /// <summary>A connected, approved machine.</summary>
    public async Task<(RelayClient Client, MachineIdentity Identity)> MemberAsync(string name, IConfiguration? settings = null)
    {
        var identity = MachineIdentity.Create();
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
        foreach (var directory in new[] { DataDirectory, DataDirectory + "-clients" })
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

    public static Envelope Sign(MachineIdentity from, string toMachine, DateTimeOffset createdOn, DateTimeOffset expiresOn,
        string body = "x", long sequence = 1) =>
        Signatures.SignEnvelope(from, Guid.NewGuid(), toMachine, sequence, expiresOn, Content(createdOn, body));
}
