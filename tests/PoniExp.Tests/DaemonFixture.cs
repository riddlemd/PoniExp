using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Daemon;

namespace PoniExp.Tests;

/// <summary>
/// A real daemon for one machine, on its own pipe or socket, against a <see cref="RouterFixture"/> router.
/// Short timeouts so tests don't wait. <see cref="RestartAsync"/> stops it and starts a new one on the same
/// keys and database, as a crash or reboot would.
/// </summary>
public sealed class DaemonFixture : IAsyncDisposable
{
    private readonly RouterFixture _router;
    private readonly IDictionary<string, string?> _settings;
    private WebApplication? _app;

    private DaemonFixture(RouterFixture router, string name, MachineKeys keys, string home, IDictionary<string, string?> settings)
    {
        _router = router;
        Name = name;
        Keys = keys;
        Home = home;
        _settings = settings;
        Api = new LocalApiClient(LocalAddress.ForHome(home));
    }

    public string Name { get; }

    public MachineKeys Keys { get; }

    public string Home { get; }

    public LocalApiClient Api { get; }

    public PoniExp.Daemon.Daemon Daemon => _app!.Services.GetRequiredService<PoniExp.Daemon.Daemon>();

    public ClientStore Store => Daemon.Store;

    public IConfigurationRoot Configuration => (IConfigurationRoot)_app!.Configuration;

    public static IDictionary<string, string?> FastSettings() => new Dictionary<string, string?>
    {
        ["Daemon:SweepInterval"] = "00:00:00.100",
        ["Daemon:EndpointTimeout"] = "00:00:02",
        ["Daemon:StreamKeepAlive"] = "00:00:00.500",
        ["Daemon:ConnectRetry"] = "00:00:00.200",
    };

    /// <summary>An approved machine called <paramref name="name"/> with its daemon running and connected.</summary>
    public static async Task<DaemonFixture> StartAsync(RouterFixture router, string name, IDictionary<string, string?>? settings = null)
    {
        var (joining, keys) = await router.MemberAsync(name);
        await joining.DisposeAsync(); // only the daemon talks to the router from here on
        var home = Path.Combine(Path.GetTempPath(), "poniexp-tests", "home-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(home);
        var all = FastSettings();
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        var daemon = new DaemonFixture(router, name, keys, home, all);
        await daemon.StartAsync();
        return daemon;
    }

    public async Task StartAsync()
    {
        var context = new DaemonContext(Keys, Home, new RelayClientOptions
        {
            RouterUrl = _router.Url,
            ReconnectDelays = [TimeSpan.FromMilliseconds(100)],
        }, new ClientStore(_router.StorePath(Keys)), LocalAddress.ForHome(Home));
        _app = DaemonHost.Build(context, ["--Logging:LogLevel:Default=None"], c => c.AddInMemoryCollection(_settings));
        await DaemonHost.StartAsync(_app, context);
        await RouterFixture.Eventually(() => Daemon.Relay.IsConnected);
    }

    public async Task StopAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            await app.StopAsync();
            await app.DisposeAsync(); // disposes the RelayClient: the machine goes offline
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    /// <summary>Waits until the outgoing message's latest event (as its sender sees it) is <paramref name="state"/>.</summary>
    public Task WaitForOutgoingAsync(Guid id, string state) =>
        RouterFixture.Eventually(() => Store.GetState(Keys.MachineId, id, MessageDirection.Out) == state);

    /// <summary>Counts rows in this machine's <c>messages</c> table (for checks the API doesn't expose).</summary>
    public long CountMessages(string where, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={Store.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM messages r WHERE {where};";
        foreach (var (n, v) in parameters)
        {
            command.Parameters.AddWithValue(n, v);
        }

        return (long)command.ExecuteScalar()!;
    }

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        await StopAsync();
    }
}
