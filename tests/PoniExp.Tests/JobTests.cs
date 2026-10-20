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
    public async Task OneClaimantAtATimeAndOnlyAMachineMeetingTheRequirements()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky", Tags("gpu-box"));
        await using var dazzle = await DaemonFixture.StartAsync(router, "dazzle", Tags("gpu-box"));
        var cores = Environment.ProcessorCount;
        await RouterFixture.Eventually(() => router.Admin.List().Count(m => m.Capabilities is not null) == 4);
        var job = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Fine-tune the model", Requirements: ["tag=gpu-box", $"cores>={cores}", "memory>=1"]));
        Assert.Equal(["tag=gpu-box", $"cores>={cores}", "memory>=1"], job.Requirements);
        Assert.Equal(["tag=gpu-box"], (await wojtek.Api.JobAsync(job.Id.ToString())).Unmet); // what wojtek lacks

        var lacking = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.JobActionAsync(job.Id.ToString(), "claim", new LocalJobAction("worker")));
        Assert.Contains("doesn't meet tag=gpu-box", lacking.Message);
        await blinky.Api.JobActionAsync(job.Id.ToString(), "claim", new LocalJobAction("trainer"));
        var second = await Assert.ThrowsAsync<LocalApiException>(() => dazzle.Api.JobActionAsync(job.Id.ToString(), "claim", new LocalJobAction("trainer")));
        Assert.Contains("already claimed by", second.Message);
    }

    [Fact]
    public async Task TheRouterEnforcesRequirementsEvenIfADaemonDoesnt()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RouterFixture.Eventually(() => router.Admin.List().All(m => m.Capabilities is not null));
        var small = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Test on a small machine", Requirements: ["memory<=1"])); // no test host is this small

        var claim = wojtek.Daemon.Relay.SignJobAction(new JobAction(Guid.NewGuid(), small.Id, JobActionKind.Claim, $"{wojtek.Keys.MachineId}/worker", null,
            null, null, null, DateTimeOffset.UtcNow, "", ""));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => wojtek.Daemon.Relay.SubmitJobActionAsync(claim));
        Assert.Contains("doesn't meet memory<=1", refused.Message);
        Assert.Contains("lacks memory<=1", (await wojtek.Api.JobsAsync()).Single().Unmet.Select(u => $"lacks {u}").Single());
    }

    [Fact]
    public void RequirementsMatchMinimumsMaximumsTextAndCards()
    {
        var big = new MachineCapabilities("linux", "6.8", "x64", "AMD Ryzen 9 7950X", 32, 64, [new GpuInfo("NVIDIA GeForce RTX 3090 Ti", 24)], ["cuda"],
            DateTimeOffset.UtcNow);
        var small = big with { Cpu = "Intel Celeron N4020", Cores = 2, MemoryGb = 4, Gpus = [], Tags = [] };
        bool Meets(MachineCapabilities m, params string[] requirements) => JobRequirement.Unmet([.. requirements.Select(JobRequirement.Parse)], m).Count == 0;

        Assert.False(Meets(big, "memory<=8")); // the 64 GB machine doesn't take the low-end job
        Assert.True(Meets(small, "memory>=2", "memory<=8"));
        Assert.True(Meets(big, "memory>=64", "os=Linux", "arch=x64", "cpu~ryzen", "cores>=16", "gpu~rtx", "gpu-memory>=16", "gpus=1", "tag=cuda"));
        Assert.False(Meets(small, "gpu~rtx"));
        Assert.True(Meets(small, "gpus=0", "tag!=cuda", "cpu!=AMD Ryzen 9 7950X"));
        Assert.False(Meets(big, "gpu-memory<=8"));
        Assert.False(JobRequirement.Parse("memory>=1").IsMetBy(null)); // a machine that reported nothing meets nothing
        Assert.Throws<FormatException>(() => JobRequirement.Parse("ram>=8"));
        Assert.Throws<FormatException>(() => JobRequirement.Parse("os>=linux"));
        Assert.Throws<FormatException>(() => JobRequirement.Parse("memory>=lots"));
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
    public async Task JobReadyReachesOnlineAgentsOnMachinesThatMayClaimIt()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek", Tags("gpu-box"));
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        var gpu = new List<LocalEvent>();
        var plain = new List<LocalEvent>();
        await using var gpuAgent = new AgentConnection(wojtek.Api, "trainer", e => { lock (gpu) { gpu.Add(e); } return Task.CompletedTask; });
        await using var plainAgent = new AgentConnection(blinky.Api, "plain", e => { lock (plain) { plain.Add(e); } return Task.CompletedTask; });
        await gpuAgent.Registered.WaitAsync(TimeSpan.FromSeconds(10));
        await plainAgent.Registered.WaitAsync(TimeSpan.FromSeconds(10));

        await ise.Api.PostJobAsync(new LocalJobPost("ops", "Needs the GPU box", Requirements: ["tag=gpu-box"]));
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
        Assert.Equal(["Anyone", "Needs the GPU box"], Ready(gpu));
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

        await router.Admin.RemoveJobAsync(open.Id.ToString()[..8]); // the owner takes one off the board
        Assert.Empty(await ise.Api.JobsAsync(all: true));
        await RouterFixture.Eventually(() => ise.Store.GetOpenPostedJobs().Count == 0); // and its poster won't re-post it
    }

    [Fact]
    public async Task ABackupRestoresLiveJobsAndPostersRepostWhatARouterLost()
    {
        var data = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        var router = await RouterFixture.StartAsync(dataDirectory: data, port: RouterFixture.RestartablePort());
        var port = router.Url.Port;
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        var first = await ise.Api.PostJobAsync(new LocalJobPost("ops", "First", "details"));
        var second = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Second", Requirements: ["memory>=1"], Backlog: true));
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
        Assert.Equal(("details", JobColumn.Backlog, "memory>=1"), (reposted[0].Description, reposted[1].Column, reposted[1].Requirements.Single().ToString()));
        Assert.Contains(ise.Store.GetDiagnostics(), d => d.Detail.Contains("re-posted job", StringComparison.Ordinal));
        await router.DisposeAsync();
    }

    /// <summary>Daemon settings giving the machine capability tags (every test daemon runs on the same hardware).</summary>
    private static Dictionary<string, string?> Tags(params string[] tags) =>
        tags.Select((t, i) => (t, i)).ToDictionary(x => $"Daemon:CapabilityTags:{x.i}", x => (string?)x.t);
}
