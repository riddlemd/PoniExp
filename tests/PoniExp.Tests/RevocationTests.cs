using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

/// <summary>Phase 11: revocation, signed by the owner and enforced by every machine.</summary>
public class RevocationTests
{
    [Fact]
    public async Task ARevokedMachineIsRefusedByTheRouterAndByDaemonsEvenIfRelisted()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek, blinky, owner, routerId) = (setup.Ise, setup.Wojtek, setup.Blinky, setup.Owner, setup.RouterId);
        var blinkyId = blinky.Keys.MachineId;
        var claimed = await ise.Api.PostJobAsync(new LocalJobPost("ops", "Held by blinky"));
        await blinky.Api.JobActionAsync(claimed.Id.ToString(), "claim", new LocalJobAction("worker"));
        var posted = await blinky.Api.PostJobAsync(new LocalJobPost("ops", "Posted by blinky"));
        Assert.Equal(0, (await wojtek.Api.AdminAsync(owner.Admin(routerId, ["group", "set", "team", "wojtek/agent", "blinky/agent"]))).ExitCode);

        Assert.Contains("full id", (await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.AdminAsync(owner.Admin(routerId, ["revoke", "blinky"])))).Message);
        var revoked = await wojtek.Api.AdminAsync(owner.Admin(routerId, ["revoke", blinkyId]));
        Assert.Equal(0, revoked.ExitCode);
        Assert.Contains("revoked blinky", revoked.Output);

        // The router: refused for good.
        await RouterFixture.Eventually(() => !blinky.Daemon.Relay.IsConnected);
        Assert.Equal(MembershipStatus.Rejected, setup.Router.Admin.List().Single(m => m.Id == blinkyId).Status);
        Assert.Contains("revoked", Assert.Throws<PoniExp.Server.RouterException>(() => setup.Router.Admin.ApproveAsync(blinkyId).GetAwaiter().GetResult()).Message);

        // Every machine: refused there too, even if a (compromised) router lists it again.
        await RouterFixture.EventuallyAsync(async () => ise.Daemon.RevokedMachines.Contains(blinkyId) && (await ise.Api.MachinesAsync()).All(m => m.Id != blinkyId));
        var record = setup.Router.App.Services.GetRequiredService<PoniExp.Server.Machines.MachineRegistry>().Get(blinkyId)!;
        await setup.Router.App.Services.GetRequiredService<PoniExp.Server.Machines.MachineRegistry>().RestoreAsync([record with { Status = MembershipStatus.Approved }]);
        await ise.Daemon.Relay.RefreshMachinesAsync();
        Assert.Null(ise.Daemon.Relay.Machine(blinkyId));

        // What waited on it.
        var held = await ise.Api.JobAsync(claimed.Id.ToString());
        Assert.Equal(("ready", null), (held.Column, held.Claimant));
        Assert.Contains(held.History, h => h.What.Contains("claimant's machine was revoked"));
        var orphan = await ise.Api.JobActionAsync(posted.Id.ToString(), "cancel", new LocalJobAction("ops")); // anyone may cancel it now
        Assert.Equal("failed", orphan.Column);
        var sent = await ise.Api.SendAsync(new LocalSendRequest("default", ["group:team"], "hello team")); // its group members are skipped
        Assert.Equal("wojtek/agent", Assert.Single(sent).To);
    }

    [Fact]
    public async Task RevokingTheDeskEndsTheDeskAndAnUnsignedRevocationIsntEnforcedByMachines()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, blinky, owner) = (setup.Ise, setup.Blinky, setup.Owner);
        var now = DateTimeOffset.UtcNow;
        await blinky.Api.HereAsync(owner.Here(blinky.Keys.MachineId, now, now.AddHours(1)));
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.OwnerAsync()).DeskName == "blinky");

        await setup.Router.Admin.RevokeAsync("blinky"); // in-process, unsigned: the router acts on it, machines don't take it as the owner's word
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.OwnerAsync()).DeskMachine is null);
        await Task.Delay(300);
        Assert.DoesNotContain(blinky.Keys.MachineId, ise.Daemon.RevokedMachines);
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required RouterFixture Router { get; init; }
        public required DaemonFixture Ise { get; init; }
        public required DaemonFixture Wojtek { get; init; }
        public required DaemonFixture Blinky { get; init; }
        public required string RouterId { get; init; }
        public TestOwner Owner { get; } = TestOwner.Create();

        public static async Task<Setup> StartAsync()
        {
            var router = await RouterFixture.StartAsync(adminPort: RouterFixture.RestartablePort());
            var ise = await DaemonFixture.StartAsync(router, "ise");
            var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
            var blinky = await DaemonFixture.StartAsync(router, "blinky");
            var setup = new Setup { Router = router, Ise = ise, Wojtek = wojtek, Blinky = blinky, RouterId = (await wojtek.Api.OwnerAsync()).RouterId! };
            router.Admin.SetOwner(setup.Owner.Key.ToString());
            foreach (var d in new[] { setup.Ise, setup.Wojtek, setup.Blinky })
            {
                await d.Api.PinOwnerAsync(setup.Owner.Key.ToString());
            }

            return setup;
        }

        public async ValueTask DisposeAsync()
        {
            await Blinky.DisposeAsync();
            await Wojtek.DisposeAsync();
            await Ise.DisposeAsync();
            await Router.DisposeAsync();
        }
    }
}
