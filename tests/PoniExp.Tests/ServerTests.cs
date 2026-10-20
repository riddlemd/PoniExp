using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server.Admin;
using PoniExp.Server.Machines;
using PoniExp.Server.Security;

namespace PoniExp.Tests;

/// <summary>Phase 4: the router's SQLite store, connection and join limits, join expiry, health and backups.</summary>
public class ServerTests
{
    private static readonly MachineDescription Description = new("TestOS 1", "x64", "test-host");

    // ---- storage ----

    [Fact]
    public async Task TheRoutersStateSurvivesARestart()
    {
        var data = NewDataDirectory();
        await using var first = await RouterFixture.StartAsync(dataDirectory: data);
        var (ise, _) = await first.MemberAsync("ise");
        var (wojtek, _) = await first.MemberAsync("wojtek");
        await wojtek.DisposeAsync();
        await ise.SendAsync("wojtek", new OutgoingMessage("while you sleep"));
        await RouterFixture.Eventually(() => first.Queue.Count == 1);
        await first.App.StopAsync();

        await using var second = await RouterFixture.StartAsync(dataDirectory: data);
        Assert.Equal(["ise", "wojtek"], second.Admin.List().Where(m => m.Status == MembershipStatus.Approved).Select(m => m.Name).Order());
        Assert.Equal(1, second.Queue.Count);
        Assert.True(File.Exists(Path.Combine(data, "router.db")));
    }

    [Fact]
    public async Task JsonFilesFromAnEarlierVersionAreImportedOnce()
    {
        var data = Directory.CreateDirectory(NewDataDirectory()).FullName;
        using var keys = MachineKeys.Create();
        var record = new MachineRecord(keys.MachineId, "ise", Description, keys.PublicKey, keys.EncryptionPublicKey, keys.EncryptionKeySignature,
            MembershipStatus.Approved, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-3), EncryptionKeyIssuedOn: keys.EncryptionKeyIssuedOn);
        var json = WireProtocol.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(Path.Combine(data, "machines.json"), JsonSerializer.Serialize(new[] { record }, json));
        var envelope = Envelopes.Sign(keys, keys, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        await File.WriteAllTextAsync(Path.Combine(data, "queue.json"), JsonSerializer.Serialize(new[]
        {
            new MessageQueue.QueuedEnvelope(envelope, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
        }, json));

        await using (var router = await RouterFixture.StartAsync(dataDirectory: data))
        {
            Assert.Equal("ise", router.Admin.List().Single().Name);
            Assert.Equal(1, router.Queue.Count);
            Assert.False(File.Exists(Path.Combine(data, "machines.json")));
            Assert.True(File.Exists(Path.Combine(data, "machines.json.imported")));
            router.KeepData = true;
        }

        await using var again = await RouterFixture.StartAsync(dataDirectory: data); // not imported twice
        Assert.Equal("ise", again.Admin.List().Single().Name);
    }

    // ---- connection limits ----

    [Fact]
    public async Task AConnectionThatDoesntAuthenticateInTimeIsDropped()
    {
        await using var router = await RouterFixture.StartAsync(settings: Settings(("AuthenticationTimeout", "00:00:00.500")));
        var idle = await RelayTests.RawConnectionAsync(router);
        var joining = await RelayTests.RawConnectionAsync(router);
        using var keys = MachineKeys.Create();
        Assert.Equal(MembershipStatus.Pending,
            (await joining.InvokeAsync<AuthResult>(HubMethods.RequestJoin, Signatures.SignJoin(keys, "ise", Description, DateTimeOffset.UtcNow))).Status);

        await RouterFixture.Eventually(() => idle.State == HubConnectionState.Disconnected);
        await Task.Delay(500);
        Assert.Equal(HubConnectionState.Connected, joining.State); // a join waiting for approval may stay
        await joining.DisposeAsync();
    }

    [Fact]
    public async Task OnlySoManyUnauthenticatedConnectionsMayWait()
    {
        await using var router = await RouterFixture.StartAsync(settings: Settings(("MaxUnauthenticatedConnections", "2"),
            ("MaxUnauthenticatedPerAddress", "2")));
        var first = await RelayTests.RawConnectionAsync(router);
        var second = await RelayTests.RawConnectionAsync(router);
        var third = await TryConnectAsync(router);
        Assert.Null(third);

        await first.DisposeAsync(); // a slot frees up
        await RouterFixture.EventuallyAsync(async () => await TryConnectAsync(router) is { } c && await Dispose(c));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task PerAddressLimitsDontApplyToASharedAddress()
    {
        var limits = new[] { ("MaxUnauthenticatedConnections", "5"), ("MaxUnauthenticatedPerAddress", "1") };
        await using (var router = await RouterFixture.StartAsync(settings: Settings(limits)))
        {
            await using var first = await RelayTests.RawConnectionAsync(router);
            Assert.Null(await TryConnectAsync(router)); // a second from 127.0.0.1
        }

        await using (var shared = await RouterFixture.StartAsync(settings: Settings([.. limits, ("SharedAddresses:0", "127.0.0.1")])))
        {
            await using var first = await RelayTests.RawConnectionAsync(shared);
            await using var second = await RelayTests.RawConnectionAsync(shared); // 127.0.0.1 stands for many clients
            Assert.Equal(2, shared.Gate.Unauthenticated);
        }
    }

    [Fact]
    public async Task AMachineMayHoldOnlySoManyConnectionsAndTheSettingAppliesLive()
    {
        await using var router = await RouterFixture.StartAsync(settings: Settings(("MaxConnectionsPerMachine", "2")));
        var (_, keys) = await router.MemberAsync("ise"); // its client holds one
        await using var second = await RelayTests.RawMemberConnectionAsync(router, keys);

        var third = await RelayTests.RawConnectionAsync(router);
        var refused = await Assert.ThrowsAsync<HubException>(() => Authenticate(third, keys));
        Assert.Contains("as many connections", refused.Message);

        router.SetSetting("PoniExp:MaxConnectionsPerMachine", "3");
        Assert.Equal(MembershipStatus.Approved, (await Authenticate(third, keys)).Status);
        await third.DisposeAsync();
    }

    // ---- joining ----

    [Fact]
    public async Task OnlySoManyNewJoinRequestsAnHour()
    {
        await using var router = await RouterFixture.StartAsync(settings: Settings(("JoinRequestsPerHour", "2"), ("JoinRequestsPerHourPerAddress", "2"),
            ("SharedAddresses:0", "127.0.0.1")));
        using var a = MachineKeys.Create();
        using var b = MachineKeys.Create();
        using var c = MachineKeys.Create();
        Assert.Equal(MembershipStatus.Pending, (await JoinAsync(router, a, "aaa")).Status);
        Assert.Equal(MembershipStatus.Pending, (await JoinAsync(router, b, "bbb")).Status);
        Assert.Equal(MembershipStatus.Pending, (await JoinAsync(router, a, "aaa")).Status); // asking again isn't a new request

        var refused = await Assert.ThrowsAsync<HubException>(() => JoinAsync(router, c, "ccc"));
        Assert.Contains("too many join requests this hour", refused.Message);
    }

    [Fact]
    public async Task OnlySoManyJoinRequestsFromOneAddress()
    {
        await using var router = await RouterFixture.StartAsync(settings: Settings(("JoinRequestsPerHour", "5"), ("JoinRequestsPerHourPerAddress", "1")));
        using var a = MachineKeys.Create();
        using var b = MachineKeys.Create();
        await JoinAsync(router, a, "aaa");
        var refused = await Assert.ThrowsAsync<HubException>(() => JoinAsync(router, b, "bbb"));
        Assert.Contains("from 127.0.0.1", refused.Message);
    }

    [Fact]
    public async Task AJoinRequestExpiresAndItsWaitingClientIsTold()
    {
        await using var router = await RouterFixture.StartAsync(settings: Settings(("JoinRequestExpiry", "00:00:01"), ("MaintenanceInterval", "00:00:00.200")));
        using var keys = MachineKeys.Create();
        var client = router.Client(keys);
        await client.ConnectAsync();
        var told = Wait.For<MembershipStatus>(h => client.MembershipChanged += h);
        Assert.Equal(MembershipStatus.Pending, await client.RequestJoinAsync("ise", Description));

        Assert.Equal(MembershipStatus.Unknown, await told);
        Assert.Empty(router.Admin.List());
    }

    [Fact]
    public async Task ClearPendingForgetsEveryWaitingRequest()
    {
        await using var router = await RouterFixture.StartAsync();
        using var a = MachineKeys.Create();
        using var b = MachineKeys.Create();
        var (_, approved) = await router.MemberAsync("ise");
        var waiting = router.Client(a);
        await waiting.ConnectAsync();
        var told = Wait.For<MembershipStatus>(h => waiting.MembershipChanged += h);
        await waiting.RequestJoinAsync("aaa", Description);
        await JoinAsync(router, b, "bbb");

        var cleared = await router.Admin.ClearPendingAsync();

        Assert.Equal(["aaa", "bbb"], cleared.Select(m => m.Name).Order());
        Assert.Equal(MembershipStatus.Unknown, await told);
        Assert.Equal(approved.MachineId, router.Admin.List().Single().Id);
    }

    // ---- health and backups ----

    [Fact]
    public async Task HealthSaysOnlyThatTheRouterWorks()
    {
        await using var router = await RouterFixture.StartAsync();
        using var http = new HttpClient { BaseAddress = router.Url };
        var health = await http.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("healthy", health.GetProperty("status").GetString());
        Assert.Single(health.EnumerateObject());
    }

    [Fact]
    public async Task ABackupRestoredIntoAnEmptyRouterBringsBackItsCertificateAndMachines()
    {
        var (from, to) = (NewDataDirectory(), NewDataDirectory());
        RouterBackup backup;
        string sha;
        IReadOnlyList<string> approved;
        await using (var original = await RouterFixture.StartAsync(useHttps: true, dataDirectory: from))
        {
            sha = original.App.Services.GetRequiredService<RouterCertificateInfo>().Sha256!;
            var (ise, _) = await PlainMember(original, "ise");
            await PlainMember(original, "wojtek");
            using var pending = MachineKeys.Create();
            await JoinAsync(original, pending, "pending", https: true);
            backup = original.Admin.Backup(original.App.Services.GetRequiredService<RouterCertificateInfo>());
            approved = [.. original.Admin.List().Where(m => m.Status == MembershipStatus.Approved).Select(m => m.Id).Order()];
        }

        Assert.Equal(2, backup.Machines.Count); // approved only
        await using (var empty = await RouterFixture.StartAsync(useHttps: true, dataDirectory: to))
        {
            var current = empty.App.Services.GetRequiredService<RouterCertificateInfo>();
            Assert.NotEqual(sha, current.Sha256);
            var result = await empty.Admin.RestoreAsync(backup, current);
            Assert.True(result.CertificateChanged);
            Assert.Equal(approved, empty.Admin.List().Select(m => m.Id).Order());
            empty.KeepData = true;
        }

        await using var restarted = await RouterFixture.StartAsync(useHttps: true, dataDirectory: to);
        Assert.Equal(sha, restarted.App.Services.GetRequiredService<RouterCertificateInfo>().Sha256);
        Assert.Equal(approved, restarted.Admin.List().Select(m => m.Id).Order());

        var damaged = backup with { CertificateSha256 = new string('0', 64) };
        await Assert.ThrowsAsync<PoniExp.Server.RouterException>(() => restarted.Admin.RestoreAsync(damaged, new RouterCertificateInfo(sha)));
    }

    [Fact]
    public async Task BadLimitSettingsStopStartup()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => RouterFixture.StartAsync(settings: Settings(("SharedAddresses:0", "not-an-ip"))));
        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            RouterFixture.StartAsync(settings: Settings(("JoinRequestsPerHour", "2"), ("JoinRequestsPerHourPerAddress", "3"))));
    }

    // ---- helpers ----

    private static Dictionary<string, string?> Settings(params (string Key, string Value)[] settings) =>
        settings.ToDictionary(s => "PoniExp:" + s.Key, s => (string?)s.Value);

    private static string NewDataDirectory() => Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));

    private static async Task<HubConnection?> TryConnectAsync(RouterFixture router)
    {
        try
        {
            var connection = await RelayTests.RawConnectionAsync(router);
            await Task.Delay(200); // a refused connection is aborted right after it opens
            await connection.InvokeAsync<HelloResult>(HubMethods.Hello, WireProtocol.Version);
            return connection;
        }
        catch (Exception e) when (e is HubException or InvalidOperationException or IOException or OperationCanceledException
                                       or System.Net.WebSockets.WebSocketException or HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<bool> Dispose(HubConnection connection)
    {
        await connection.DisposeAsync();
        return true;
    }

    private static async Task<AuthResult> Authenticate(HubConnection connection, MachineKeys keys)
    {
        var challenge = await connection.InvokeAsync<string>(HubMethods.GetChallenge);
        return await connection.InvokeAsync<AuthResult>(HubMethods.Authenticate, keys.PublicKey,
            keys.Sign(Signatures.AuthPayload(challenge, keys.PublicKey)));
    }

    private static async Task<AuthResult> JoinAsync(RouterFixture router, MachineKeys keys, string name, bool https = false)
    {
        var connection = https ? await PinnedConnectionAsync(router) : await RelayTests.RawConnectionAsync(router);
        return await connection.InvokeAsync<AuthResult>(HubMethods.RequestJoin, Signatures.SignJoin(keys, name, Description, DateTimeOffset.UtcNow));
    }

    private static async Task<HubConnection> PinnedConnectionAsync(RouterFixture router)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(router.Url, HubMethods.Path), http =>
            {
                http.HttpMessageHandlerFactory = handler =>
                {
                    ((HttpClientHandler)handler).ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                    return handler;
                };
                http.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            })
            .AddJsonProtocol(json => WireProtocol.Configure(json.PayloadSerializerOptions))
            .Build();
        await connection.StartAsync();
        await connection.InvokeAsync<HelloResult>(HubMethods.Hello, WireProtocol.Version);
        return connection;
    }

    /// <summary>An approved machine on an HTTPS router (MemberAsync's client doesn't pin).</summary>
    private static async Task<(HubConnection, MachineKeys)> PlainMember(RouterFixture router, string name)
    {
        var keys = MachineKeys.Create();
        await JoinAsync(router, keys, name, https: true);
        await router.Admin.ApproveAsync(keys.MachineId);
        return (await PinnedConnectionAsync(router), keys);
    }
}
