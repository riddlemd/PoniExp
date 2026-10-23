using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server.Admin;

namespace PoniExp.Tests;

/// <summary>
/// Phases 9-11, end to end with the real <c>poniexp-client</c>: a new machine runs <c>join</c>, the desk (another machine,
/// whose SSH agent holds the owner key) answers it with <c>approvals</c> and <c>approve</c>/<c>deny</c>, or the router's own
/// <c>admin approve</c> waits for the desk; then the new machine talks.
/// </summary>
public class JoinFromTheDeskTests
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Given_ANewMachineRunningJoin_When_TheDeskListsApprovals_Then_ItShowsTheMachineAndItsJoinCode()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();

        // When
        var (exit, listed) = await ClientCli.RunAsync(setup.Desk, null, "approvals");

        // Then
        Assert.Equal(0, exit);
        Assert.Contains($"{setup.Blinky.Keys.MachineId[..8]}  blinky (new machine)", listed);
        Assert.Contains($"join code {KeyFingerprint.JoinCodeOf(setup.Blinky.Keys.PublicKey)}", listed);
        Assert.Contains("notes: the new build box", listed);
    }

    [Fact]
    public async Task Given_ANewMachineRunningJoin_When_TheDeskApprovesItsFourCharacterPrefix_Then_JoinEndsApproved()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();

        // When
        var (exit, output) = await ClientCli.RunAsync(setup.Desk, setup.Agent, "approve", setup.Blinky.Keys.MachineId[..4]);

        // Then
        Assert.True(exit == 0, output);
        Assert.Equal(0, await join.WaitAsync(JoinTimeout));
        Assert.EndsWith("approved\n", join.Output);
    }

    [Fact]
    public async Task Given_ANewMachineRunningJoin_When_TheDeskApprovesIt_Then_TheOwnerKeySignedOnce()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();

        // When
        await ClientCli.RunAsync(setup.Desk, setup.Agent, "approve", setup.Blinky.Keys.MachineId[..8]);

        // Then
        Assert.Equal(1, setup.Agent.Signed);
    }

    [Fact]
    public async Task Given_AMachineApprovedFromTheDesk_When_ItSendsToAnotherMachine_Then_TheMessageArrives()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        await setup.JoinThroughTheDeskAsync();
        await Setup.RegisterAsync(setup.Ise, "agent");

        // When
        var sent = Assert.Single(await setup.Blinky.Api.SendAsync(new LocalSendRequest("builder", ["ise/agent"], "build 42 is green")));

        // Then
        await setup.Blinky.WaitForOutgoingAsync(sent.Id, MessageEvents.Received);
        var received = Assert.Single(await setup.Ise.Api.InboxAsync("agent"));
        Assert.Equal(("blinky/builder", "build 42 is green"), (received.From, received.Body));
    }

    [Fact]
    public async Task Given_AMachineApprovedFromTheDesk_When_AnotherMachineSendsToIt_Then_TheMessageArrives()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        await setup.JoinThroughTheDeskAsync();
        await Setup.RegisterAsync(setup.Blinky, "builder");

        // When
        var sent = Assert.Single(await setup.Ise.Api.SendAsync(new LocalSendRequest("agent", ["blinky/builder"], "build 43, please")));

        // Then
        await setup.Ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Received);
        var received = Assert.Single(await setup.Blinky.Api.InboxAsync("builder"));
        Assert.Equal(("ise/agent", "build 43, please"), (received.From, received.Body));
    }

    [Fact]
    public async Task Given_ANewMachineRunningJoin_When_TheDeskDeniesIt_Then_JoinEndsNotApproved()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();

        // When
        var (exit, output) = await ClientCli.RunAsync(setup.Desk, setup.Agent, "deny", setup.Blinky.Keys.MachineId[..4]);

        // Then
        Assert.True(exit == 0, output);
        Assert.Equal(1, await join.WaitAsync(JoinTimeout));
        Assert.Contains("not approved", join.Output);
    }

    [Fact]
    public async Task Given_ANewMachineRunningJoin_When_TheDeskDeniesIt_Then_TheRouterForgetsTheRequest()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();

        // When
        await ClientCli.RunAsync(setup.Desk, setup.Agent, "deny", setup.Blinky.Keys.MachineId[..8]);

        // Then
        Assert.DoesNotContain(setup.Router.Admin.List(), m => m.Id == setup.Blinky.Keys.MachineId);
    }

    [Fact]
    public async Task Given_ANewMachineRunningJoin_When_TheDeskApprovesAThreeCharacterPrefix_Then_ItStaysPending()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();

        // When
        var (exit, _) = await ClientCli.RunAsync(setup.Desk, setup.Agent, "approve", setup.Blinky.Keys.MachineId[..3]);

        // Then
        Assert.NotEqual(0, exit);
        Assert.Equal(0, setup.Agent.Signed);
        Assert.Equal(MembershipStatus.Pending, setup.Router.Admin.List().Single(m => m.Id == setup.Blinky.Keys.MachineId).Status);
    }

    [Fact]
    public async Task Given_AJoinStoppedWithCtrlC_When_TheDeskApprovesItLater_Then_TheMachineIsApproved()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using (var join = await setup.StartJoinAsync())
        {
            join.Interrupt();
            Assert.Equal(130, await join.WaitAsync(JoinTimeout));
        }

        // When
        var (exit, output) = await ClientCli.RunAsync(setup.Desk, setup.Agent, "approve", setup.Blinky.Keys.MachineId[..8]);

        // Then
        Assert.True(exit == 0, output);
        Assert.Equal("approved", (await setup.Blinky.Api.StatusAsync()).Membership);
    }

    [Fact]
    public async Task Given_ARouterSideAdminApproveWaitingOnTheDesk_When_TheDeskApprovesItsCard_Then_JoinEndsApproved()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();
        var dockerExec = setup.DockerExecAsync("approve", setup.Blinky.Keys.MachineId[..8]);
        var card = await RouterFixture.EventuallyValueAsync(async () => (await setup.Desk.Api.DeskAsync()).SingleOrDefault(a => a.Direction == "admin"));

        // When
        var (exit, output) = await ClientCli.RunAsync(setup.Desk, setup.Agent, "approve", card.Id.ToString()[..4]);

        // Then
        Assert.True(exit == 0, output);
        Assert.Equal(0, await join.WaitAsync(JoinTimeout));
        Assert.Equal(0, (await dockerExec.WaitAsync(JoinTimeout)).Exit);
    }

    [Fact]
    public async Task Given_ARouterSideAdminApproveWaitingOnTheDesk_When_TheDeskDeniesItsCard_Then_TheMachineStaysPending()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();
        var dockerExec = setup.DockerExecAsync("approve", setup.Blinky.Keys.MachineId[..8]);
        var card = await RouterFixture.EventuallyValueAsync(async () => (await setup.Desk.Api.DeskAsync()).SingleOrDefault(a => a.Direction == "admin"));

        // When
        var (exit, output) = await ClientCli.RunAsync(setup.Desk, setup.Agent, "deny", card.Id.ToString()[..4]);

        // Then
        Assert.True(exit == 0, output);
        var (dockerExit, dockerOutput) = await dockerExec.WaitAsync(JoinTimeout);
        Assert.Equal(1, dockerExit);
        Assert.Contains("denied on the desk", dockerOutput);
        Assert.Equal(MembershipStatus.Pending, setup.Router.Admin.List().Single(m => m.Id == setup.Blinky.Keys.MachineId).Status);
    }

    [Fact]
    public async Task Given_ANewMachineRunningJoin_When_TheDeskRunsAdminApproveForIt_Then_JoinEndsApproved()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        using var join = await setup.StartJoinAsync();

        // When
        var (exit, output) = await ClientCli.RunAsync(setup.Desk, setup.Agent, "admin", "approve", setup.Blinky.Keys.MachineId[..8]);

        // Then
        Assert.True(exit == 0, output);
        Assert.Equal(0, await join.WaitAsync(JoinTimeout));
    }

    /// <summary>
    /// A router with an owner key and its admin listener; wojtek the desk (owner key pinned, `here` signed); ise, another
    /// member; blinky, a new machine whose daemon runs but hasn't asked to join; the owner's key in a fake SSH agent.
    /// </summary>
    private sealed class Setup : IAsyncDisposable
    {
        public required RouterFixture Router { get; init; }
        public required DaemonFixture Desk { get; init; }
        public required DaemonFixture Ise { get; init; }
        public required DaemonFixture Blinky { get; init; }
        public required FakeSshAgent Agent { get; init; }
        public required string AdminUrl { get; init; }

        public static async Task<Setup> StartAsync()
        {
            var port = RouterFixture.RestartablePort();
            var router = await RouterFixture.StartAsync(adminPort: port);
            var ise = await DaemonFixture.StartAsync(router, "ise");
            var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
            var owner = TestOwner.Create();
            router.Admin.SetOwner(owner.Key.ToString());
            await ise.Api.PinOwnerAsync(owner.Key.ToString());
            await wojtek.Api.PinOwnerAsync(owner.Key.ToString());
            var now = DateTimeOffset.UtcNow;
            await wojtek.Api.HereAsync(owner.Here(wojtek.Keys.MachineId, now, now.AddHours(1)));
            var blinky = await DaemonFixture.StartUnjoinedAsync(router, "blinky");
            return new Setup { Router = router, Desk = wojtek, Ise = ise, Blinky = blinky, Agent = FakeSshAgent.Start(owner), AdminUrl = $"http://127.0.0.1:{port}" };
        }

        /// <summary>`poniexp-client join blinky` on blinky, once its request waits on the desk.</summary>
        public async Task<ClientCli> StartJoinAsync()
        {
            var join = await ClientCli.StartAsync(Blinky, null, "join", "blinky", "--notes", "the new build box");
            await RouterFixture.EventuallyAsync(async () => (await Desk.Api.DeskAsync()).Any(a => a.Direction == "join"));
            await join.WaitForOutputAsync("waiting (Ctrl+C to stop; the request stays pending)");
            return join;
        }

        public async Task JoinThroughTheDeskAsync()
        {
            using var join = await StartJoinAsync();
            await ClientCli.RunAsync(Desk, Agent, "approve", Blinky.Keys.MachineId[..8]);
            Assert.Equal(0, await join.WaitAsync(JoinTimeout));
        }

        /// <summary>An agent the machine knows (it connected once), now offline: messages to it are stored, not refused.</summary>
        public static async Task RegisterAsync(DaemonFixture machine, string agent)
        {
            await using var connection = new AgentConnection(machine.Api, agent, _ => Task.CompletedTask);
            await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>`docker exec poniexp-server poniexp admin ...`: the same CLI, against the admin listener.</summary>
        public async Task<(int Exit, string Output)> DockerExecAsync(params string[] args)
        {
            var output = new StringWriter();
            var exit = await Task.Run(() => AdminCli.RunAsync([.. args, "--url", AdminUrl], output));
            return (exit, output.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await Agent.DisposeAsync();
            await Blinky.DisposeAsync();
            await Desk.DisposeAsync();
            await Ise.DisposeAsync();
            await Router.DisposeAsync();
        }
    }
}
