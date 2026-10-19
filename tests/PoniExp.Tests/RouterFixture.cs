using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server;
using PoniExp.Server.Admin;

namespace PoniExp.Tests;

/// <summary>A real router on a random loopback port, with its own data directory.</summary>
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

    public static async Task<RouterFixture> StartAsync(bool useHttps = false, string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        var app = RouterHost.Build(["--Logging:LogLevel:Default=None"], o =>
        {
            o.DataDirectory = dataDirectory;
            o.BindAddress = "127.0.0.1";
            o.Port = 0;
            o.AdminPort = null;
            o.UseHttps = useHttps;
        });
        await app.StartAsync();
        return new RouterFixture(app, dataDirectory);
    }

    public RelayClient Client(MachineIdentity identity, string? pin = null)
    {
        var client = new RelayClient(identity, new RelayClientOptions
        {
            RouterUrl = Url,
            PinnedCertificateSha256 = pin,
            ReconnectDelays = [TimeSpan.FromMilliseconds(100)],
        });
        _clients.Add(client);
        return client;
    }

    /// <summary>A connected, approved machine.</summary>
    public async Task<(RelayClient Client, MachineIdentity Identity)> MemberAsync(string name)
    {
        var identity = MachineIdentity.Create();
        var client = Client(identity);
        await client.ConnectAsync();
        var approved = Wait.For<MembershipStatus>(h => client.MembershipChanged += h);
        await client.RequestJoinAsync(name, new MachineDescription("TestOS 1", "x64", name + "-host"));
        await Admin.ApproveAsync(identity.MachineId);
        Assert.Equal(MembershipStatus.Approved, await approved);
        await Eventually(() => client.Machines.Any(m => m.Id == identity.MachineId));
        return (client, identity);
    }

    public static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
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
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
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
