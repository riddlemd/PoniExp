using PoniExp.Client;
using PoniExp.Core.Protocol;
using PoniExp.Server.Security;

namespace PoniExp.Tests;

/// <summary>Phase 8: the job board.</summary>
public class JobTests
{
    [Fact]
    public async Task AJobPostedOnOneMachineIsClaimedAndFinishedOnAnother()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");

        var posted = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Rebuild the index", "Run the nightly job by hand."));
        Assert.Equal(("ready", "ise/ops"), (posted.Column, posted.Poster));
        var seen = Assert.Single(await wojtek.Api.JobsAsync());
        Assert.Equal(("Rebuild the index", "Run the nightly job by hand."), (seen.Title, seen.Description));

        var claimed = await wojtek.Api.JobActionAsync(seen.Id.ToString()[..8], "claim", new LocalJobAction("worker"));
        Assert.Equal(("claimed", "wojtek/worker"), (claimed.Column, claimed.Claimant));
        Assert.NotNull(claimed.LeaseExpiresOn);
        await wojtek.Api.JobActionAsync(seen.Id.ToString(), "move", new LocalJobAction("worker", "running"));
        await wojtek.Api.JobActionAsync(seen.Id.ToString(), "comment", new LocalJobAction("worker", Text: "halfway"));
        var done = await wojtek.Api.JobActionAsync(seen.Id.ToString(), "move", new LocalJobAction("worker", "done", "index rebuilt, 1.2M rows"));

        Assert.Equal(("done", "index rebuilt, 1.2M rows"), (done.Column, done.Result));
        var history = (await ise.Api.JobAsync(posted.Id.ToString())).History;
        Assert.Equal(["posted", "claimed", "started", "commented", "finished"], history.Select(h => h.What));
        Assert.All(history.Skip(1), h => Assert.Equal("wojtek/worker", h.By));
        Assert.Empty(await ise.Api.JobsAsync()); // finished: only with all
        Assert.Single(await ise.Api.JobsAsync(all: true));
        await RouterFixture.Eventually(() => ise.Store.GetOpenPostedJobs().Count == 0); // the poster's daemon won't re-post it
    }

    [Fact]
    public async Task OneClaimantAtATimeAndARoleJobOnlyForItsHolders()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        await RegisterAndLeaveAsync(wojtek, "plain");
        await RegisterAndLeaveAsync(blinky, "trainer", ["gpu"]);
        await RegisterAndLeaveAsync(wojtek, "trainer2", ["gpu"]);
        var job = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Fine-tune the model", Role: "gpu"));

        var noRole = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.JobActionAsync(job.Id.ToString(), "claim", new LocalJobAction("plain")));
        Assert.Contains("doesn't declare the role 'gpu'", noRole.Message);
        await blinky.Api.JobActionAsync(job.Id.ToString(), "claim", new LocalJobAction("trainer"));
        var second = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.JobActionAsync(job.Id.ToString(), "claim", new LocalJobAction("trainer2")));
        Assert.Contains("already claimed by", second.Message);
    }

    [Fact]
    public async Task OnlyTheClaimantMovesAClaimedJobOnAndOnlyThePosterCancels()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var job = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Later", Backlog: true));
        Assert.Equal("backlog", job.Column);
        var id = job.Id.ToString();

        await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.JobActionAsync(id, "claim", new LocalJobAction("worker"))); // not ready yet
        await wojtek.Api.JobActionAsync(id, "move", new LocalJobAction("worker", "ready")); // anyone may
        await wojtek.Api.JobActionAsync(id, "claim", new LocalJobAction("worker"));
        var notYours = await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.JobActionAsync(id, "move", new LocalJobAction("ops", "done")));
        Assert.Contains("only the claimant", notYours.Message);
        await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.JobActionAsync(id, "release", new LocalJobAction("ops")));
        var notPoster = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.JobActionAsync(id, "cancel", new LocalJobAction("worker")));
        Assert.Contains("only the poster", notPoster.Message);

        var released = await wojtek.Api.JobActionAsync(id, "release", new LocalJobAction("worker", Text: "no time"));
        Assert.Equal(("ready", null), (released.Column, released.Claimant));
        var cancelled = await ise.Api.JobActionAsync(id, "cancel", new LocalJobAction("ops"));
        Assert.Equal(("failed", "cancelled by the poster"), (cancelled.Column, cancelled.Result));
        Assert.Contains("is failed", (await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.JobActionAsync(id, "claim", new LocalJobAction("worker")))).Message);
    }

    [Fact]
    public async Task AClaimRunsOutUnlessItsAgentIsOnline()
    {
        await using var router = await RouterFixture.StartAsync(settings: new Dictionary<string, string?> { ["PoniExp:JobLease"] = "00:00:01" });
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek", new Dictionary<string, string?> { ["Daemon:JobLeaseRenewal"] = "00:00:00.200" });
        var kept = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Kept"));
        var dropped = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Dropped"));
        await using var online = new AgentConnection(wojtek.Api, "worker", _ => Task.CompletedTask);
        await online.Registered.WaitAsync(TimeSpan.FromSeconds(10));
        await wojtek.Api.JobActionAsync(kept.Id.ToString(), "claim", new LocalJobAction("worker"));
        await wojtek.Api.JobActionAsync(dropped.Id.ToString(), "claim", new LocalJobAction("away")); // an agent that isn't connected

        await Task.Delay(1500);
        await router.Jobs.SweepAsync();

        var after = await ise.Api.JobsAsync();
        var k = after.Single(j => j.Id == kept.Id);
        Assert.Equal(("claimed", "wojtek/worker"), (k.Column, k.Claimant));
        var back = after.Single(j => j.Id == dropped.Id);
        Assert.Equal(("ready", null), (back.Column, back.Claimant));
        Assert.Equal(("router", "lease ran out"), (back.History[^1].By, back.History[^1].What));
        Assert.Equal("wojtek/away", back.History[^2].By); // who held it
    }

    [Fact]
    public async Task AForgedOrAlteredActionIsRefused()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var job = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Mine"));
        var relay = wojtek.Daemon.Relay;

        var claim = relay.SignJobAction(new JobAction(Guid.NewGuid(), job.Id, JobActionKind.Claim, $"{wojtek.Keys.MachineId}/worker", null, null, null, null,
            DateTimeOffset.UtcNow, "", ""));
        var altered = claim with { Actor = $"{wojtek.Keys.MachineId}/other" };
        Assert.Contains("signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SubmitJobActionAsync(altered))).Message);
        var asIse = relay.SignJobAction(new JobAction(Guid.NewGuid(), job.Id, JobActionKind.Cancel, $"{ise.Keys.MachineId}/ops", null, null, null, null,
            DateTimeOffset.UtcNow, "", "")); // signed by wojtek, claiming to be ise's poster
        Assert.Contains("your own machine", (await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SubmitJobActionAsync(asIse))).Message);
        var old = relay.SignJobAction(claim with { ActionId = Guid.NewGuid(), OccurredOn = DateTimeOffset.UtcNow.AddHours(-1) });
        Assert.Contains("too far", (await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SubmitJobActionAsync(old))).Message);

        Assert.Equal("ready", (await ise.Api.JobAsync(job.Id.ToString())).Column);
        await relay.SubmitJobActionAsync(claim); // the genuine one
        Assert.Equal("claimed", (await ise.Api.JobAsync(job.Id.ToString())).Column);
    }

    [Fact]
    public async Task JobReadyReachesOnlyOnlineAgentsThatMayClaimIt()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var gpu = new List<LocalEvent>();
        var plain = new List<LocalEvent>();
        await using var gpuAgent = new AgentConnection(wojtek.Api, "trainer", e => { lock (gpu) { gpu.Add(e); } return Task.CompletedTask; }, roles: ["gpu"]);
        await using var plainAgent = new AgentConnection(wojtek.Api, "plain", e => { lock (plain) { plain.Add(e); } return Task.CompletedTask; });
        await gpuAgent.Registered.WaitAsync(TimeSpan.FromSeconds(10));
        await plainAgent.Registered.WaitAsync(TimeSpan.FromSeconds(10));

        await ise.Api.PostJobAsync(new LocalJobPost("ops", "Needs a GPU", Role: "gpu"));
        await ise.Api.PostJobAsync(new LocalJobPost("ops", "Anyone"));
        await ise.Api.PostJobAsync(new LocalJobPost("ops", "Not yet", Backlog: true));

        static string[] Ready(List<LocalEvent> events)
        {
            lock (events)
            {
                return [.. events.Where(e => e.Kind == LocalEventKinds.JobReady).Select(e => e.Job!.Title).Order()];
            }
        }

        await RouterFixture.Eventually(() => Ready(gpu).Length == 2 && Ready(plain).Length == 1);
        await Task.Delay(300);
        Assert.Equal(["Anyone", "Needs a GPU"], Ready(gpu));
        Assert.Equal(["Anyone"], Ready(plain));
    }

    [Fact]
    public async Task FinishedJobsAreDroppedAfterTheirRetention()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        var finished = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Short-lived"));
        await ise.Api.JobActionAsync(finished.Id.ToString(), "cancel", new LocalJobAction("ops"));
        var open = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Still open"));

        router.SetSetting("PoniExp:JobRetention", "00:00:01");
        await Task.Delay(1200);
        await router.Jobs.SweepAsync();

        Assert.Equal([open.Id], (await ise.Api.JobsAsync(all: true)).Select(j => j.Id));
        Assert.Equal([open.Id], router.Admin.Jobs(includeFinished: true).Select(j => j.Id));
    }

    [Fact]
    public async Task ABackupRestoresLiveJobsAndPostersRepostWhatARouterLost()
    {
        var data = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        var router = await RouterFixture.StartAsync(dataDirectory: data, port: RouterFixture.RestartablePort());
        var port = router.Url.Port;
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        var first = await ise.Api.PostJobAsync(new LocalJobPost("ops", "First", "details"));
        var second = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Second", Role: "gpu", Backlog: true));
        var done = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Done already"));
        await ise.Api.JobActionAsync(done.Id.ToString(), "cancel", new LocalJobAction("ops"));
        var backup = router.Admin.Backup(router.App.Services.GetRequiredService<RouterCertificateInfo>());
        Assert.Equal(new[] { first.Id, second.Id }.Order(), backup.Jobs!.Select(j => j.Id).Order()); // live ones only

        // A backup restores the live jobs.
        await using (var restored = await RouterFixture.StartAsync())
        {
            var result = await restored.Admin.RestoreAsync(backup, restored.App.Services.GetRequiredService<RouterCertificateInfo>());
            Assert.Equal(2, result.Jobs);
            Assert.Equal(["First", "Second"], restored.Admin.Jobs(includeFinished: true).Select(j => j.Title).Order());
        }

        // The volume is lost: a new router with only the machines put back. Ise's daemon re-posts its open jobs.
        await router.App.StopAsync(); // stopped, not disposed: disposing deletes the daemons' data beside its own
        var fresh = data + "-new";
        await using (var setup = await RouterFixture.StartAsync(dataDirectory: fresh))
        {
            await setup.Admin.RestoreAsync(backup with { Jobs = null }, setup.App.Services.GetRequiredService<RouterCertificateInfo>());
            Assert.Empty(setup.Admin.Jobs(includeFinished: true));
            setup.KeepData = true;
        }

        await using var back = await RouterFixture.StartAsync(dataDirectory: fresh, port: port);
        await RouterFixture.Eventually(() => back.Admin.Jobs(includeFinished: true).Count == 2);
        var reposted = back.Admin.Jobs(includeFinished: true).OrderBy(j => j.Title).ToList();
        Assert.Equal([first.Id, second.Id], reposted.Select(j => j.Id));
        Assert.Equal(("details", JobColumn.Backlog, "gpu"), (reposted[0].Description, reposted[1].Column, reposted[1].Role));
        Assert.Contains(ise.Store.GetDiagnostics(), d => d.Detail.Contains("re-posted job", StringComparison.Ordinal));
        await router.DisposeAsync();
    }

    private static async Task RegisterAndLeaveAsync(DaemonFixture daemon, string agent, IReadOnlyList<string>? roles = null)
    {
        await using var connection = new AgentConnection(daemon.Api, agent, _ => Task.CompletedTask, roles: roles);
        await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
