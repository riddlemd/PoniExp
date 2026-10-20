using System.Text.Json;
using PoniExp.Client;
using PoniExp.Core.Protocol;
using PoniExp.Server.Admin;

namespace PoniExp.Tests;

/// <summary>Phase 11: once there's an owner key, changes run only signed; a docker exec change waits for the desk.</summary>
public class SignedOnlyTests
{
    [Fact]
    public async Task ADockerExecChangeWaitsForTheDeskAndRunsOnceSignedThere()
    {
        await using var setup = await Setup.StartAsync();
        var docker = setup.DockerExec("remove", "ise");
        var card = await RouterFixture.EventuallyValueAsync(async () => (await setup.Wojtek.Api.DeskAsync()).SingleOrDefault(a => a.Direction == "admin"));
        Assert.Equal(("docker exec on the router", "poniexp admin remove ise"), (card.From, card.Summary));
        var request = JsonSerializer.Deserialize<AdminRequest>(card.Command!, WireProtocol.Json)!;
        Assert.Contains(request.Code, card.Details);
        Assert.False(docker.IsCompleted);
        Assert.Contains(setup.Router.Admin.List(), m => m.Name == "ise"); // nothing ran yet

        Assert.Contains("approved with the owner key", (await Assert.ThrowsAsync<LocalApiException>(() => setup.Wojtek.Api.AnswerAsync(card.Id.ToString(), true))).Message);
        var forOther = setup.Owner.Approve(setup.RouterId, request with { Args = ["remove", "wojtek"] });
        Assert.Contains("different command", (await Assert.ThrowsAsync<LocalApiException>(() => setup.Wojtek.Api.AdminAsync(forOther))).Message);
        var ran = await setup.Wojtek.Api.AdminAsync(setup.Owner.Approve(setup.RouterId, request));
        Assert.Equal(0, ran.ExitCode);

        var (exit, output) = await docker.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, exit);
        Assert.Contains($"code {request.Code}", output);
        Assert.Contains("removed ise", output);
        Assert.DoesNotContain(setup.Router.Admin.List(), m => m.Name == "ise");
        await RouterFixture.EventuallyAsync(async () => (await setup.Wojtek.Api.DeskAsync()).Count == 0);
    }

    [Fact]
    public async Task DenyOrTheTimeoutCancelsIt()
    {
        await using var setup = await Setup.StartAsync(new Dictionary<string, string?> { ["PoniExp:AdminApprovalTimeout"] = "00:00:03" });
        var denied = setup.DockerExec("traffic-log", "on");
        var card = await RouterFixture.EventuallyValueAsync(async () => (await setup.Wojtek.Api.DeskAsync()).SingleOrDefault(a => a.Direction == "admin"));
        await setup.Wojtek.Api.AnswerAsync(card.Id.ToString(), approve: false);
        var (exit, output) = await denied.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal((1, true), (exit, output.Contains("denied on the desk")));

        var (timedOutExit, timedOut) = await setup.DockerExec("clear-pending").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((1, true), (timedOutExit, timedOut.Contains("timed out")));
    }

    [Fact]
    public async Task ReadsRunAtOnceAndAChangeWithoutTheTokenIsRefused()
    {
        await using var setup = await Setup.StartAsync();
        var (exit, output) = await setup.DockerExec("list").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exit);
        Assert.Contains("wojtek", output);

        using var http = new HttpClient { BaseAddress = new Uri(setup.AdminUrl + "/") }; // straight at the listener, skipping the CLI
        var refused = await http.PostAsync("admin/machines/ise/remove", null);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("needs the owner's signature", await refused.Content.ReadAsStringAsync());
        using var forged = new HttpRequestMessage(HttpMethod.Post, "admin/machines/ise/remove");
        forged.Headers.Add(PoniExp.Server.Owner.AdminApprovals.TokenHeader, new string('0', 64));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task WithNoOwnerKeyEverythingRunsAndALostKeyCanBeReplaced()
    {
        var adminPort = RouterFixture.RestartablePort();
        await using var router = await RouterFixture.StartAsync(adminPort: adminPort);
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var url = $"http://127.0.0.1:{adminPort}";
        var owner = TestOwner.Create();
        Assert.Equal(0, await Run(url, "group", "set", "oncall", "wojtek/agent")); // no owner key yet: unsigned is fine
        Assert.Equal(0, await Run(url, ["owner", "set", .. owner.Key.ToString().Split(' ')]));
        await wojtek.Api.PinOwnerAsync(owner.Key.ToString());

        var replacement = TestOwner.Create();
        var output = new StringWriter();
        Assert.Equal(0, await AdminCli.RunAsync(["owner", "set", "--lost-key", .. replacement.Key.ToString().Split(' '), "--url", url], output));
        Assert.Contains("owner pin", output.ToString());
        Assert.Equal(replacement.Key.Fingerprint, router.Admin.Owner().Fingerprint);
        await Task.Delay(300);
        var seen = await wojtek.Api.OwnerAsync();
        Assert.Equal(owner.Key.Fingerprint, seen.PinnedFingerprint); // not followed
        Assert.NotEqual(seen.RouterFingerprint, seen.PinnedFingerprint);
    }

    [Fact]
    public async Task TheFirstMachineApprovesItselfWithTheOwnerKey()
    {
        await using var router = await RouterFixture.StartAsync();
        var owner = TestOwner.Create();
        router.Admin.SetOwner(owner.Key.ToString()); // no machine, no desk: nobody else could approve it
        using var keys = PoniExp.Core.Crypto.MachineKeys.Create();
        var client = router.Client(keys);
        await client.ConnectAsync();
        var approved = Wait.For<MembershipStatus>(h => client.MembershipChanged += h);
        await client.RequestJoinAsync("wojtek", new MachineDescription("TestOS 1", "x64", "wojtek-host"));
        Assert.Equal(owner.Key.ToString(), client.RouterOwnerKey);

        var other = Assert.ThrowsAsync<InvalidOperationException>(() => client.ApproveOwnJoinAsync(owner.Admin(client.RouterId!, ["approve", new string('b', 32)])));
        Assert.Contains("isn't for approving this machine", (await other).Message);
        Assert.Contains("isn't signed by the owner", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ApproveOwnJoinAsync(TestOwner.Create().Admin(client.RouterId!, ["approve", keys.MachineId])))).Message);
        await client.ApproveOwnJoinAsync(owner.Admin(client.RouterId!, ["approve", keys.MachineId]));
        Assert.Equal(MembershipStatus.Approved, await approved);
    }

    private static Task<int> Run(string url, params string[] args) => AdminCli.RunAsync([.. args, "--url", url], new StringWriter());

    private sealed class Setup : IAsyncDisposable
    {
        public required RouterFixture Router { get; init; }
        public required DaemonFixture Ise { get; init; }
        public required DaemonFixture Wojtek { get; init; }
        public required string RouterId { get; init; }
        public required string AdminUrl { get; init; }
        public TestOwner Owner { get; } = TestOwner.Create();

        /// <summary>A desk on wojtek; ise to act on.</summary>
        public static async Task<Setup> StartAsync(IDictionary<string, string?>? settings = null)
        {
            var port = RouterFixture.RestartablePort();
            var router = await RouterFixture.StartAsync(adminPort: port, settings: settings);
            var ise = await DaemonFixture.StartAsync(router, "ise");
            var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
            var setup = new Setup
            {
                Router = router, Ise = ise, Wojtek = wojtek, RouterId = (await wojtek.Api.OwnerAsync()).RouterId!, AdminUrl = $"http://127.0.0.1:{port}",
            };
            router.Admin.SetOwner(setup.Owner.Key.ToString());
            await wojtek.Api.PinOwnerAsync(setup.Owner.Key.ToString());
            var now = DateTimeOffset.UtcNow;
            await wojtek.Api.HereAsync(setup.Owner.Here(wojtek.Keys.MachineId, now, now.AddHours(1)));
            return setup;
        }

        /// <summary>`docker exec poniexp-server poniexp admin ...`: the same CLI, against the admin listener.</summary>
        public async Task<(int Exit, string Output)> DockerExec(params string[] args)
        {
            var output = new StringWriter();
            var exit = await Task.Run(() => AdminCli.RunAsync([.. args, "--url", AdminUrl], output));
            return (exit, output.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await Wojtek.DisposeAsync();
            await Ise.DisposeAsync();
            await Router.DisposeAsync();
        }
    }
}
