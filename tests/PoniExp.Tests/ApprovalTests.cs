using System.Text.Json;
using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

/// <summary>Phase 9: the owner's desk and approvals.</summary>
public class ApprovalTests
{
    [Fact]
    public async Task OnlyADeskSignedByThePinnedOwnerCounts()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var owner = TestOwner.Create();
        var now = DateTimeOffset.UtcNow;

        var refused = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.HereAsync(owner.Here(wojtek.Keys.MachineId, now, now.AddHours(1))));
        Assert.Contains("no owner key is pinned", refused.Message);
        router.Admin.SetOwner(owner.Key.ToString());
        await ise.Api.PinOwnerAsync(owner.Key.ToString());
        await wojtek.Api.PinOwnerAsync(owner.Key.ToString());
        Assert.Contains("isn't the router's owner key", (await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.PinOwnerAsync(TestOwner.Create().Key.ToString()))).Message);

        var stranger = TestOwner.Create();
        Assert.Contains("not signed by the owner", (await Assert.ThrowsAsync<LocalApiException>(() =>
            wojtek.Api.HereAsync(stranger.Here(wojtek.Keys.MachineId, now, now.AddHours(1))))).Message);
        await Assert.ThrowsAsync<PoniExp.Server.RouterException>(() =>
            router.Desk.SetHereAsync(wojtek.Keys.MachineId, stranger.Here(wojtek.Keys.MachineId, DateTimeOffset.UtcNow, now.AddHours(1))));
        await Assert.ThrowsAsync<PoniExp.Server.RouterException>(() =>
            router.Desk.SetHereAsync(wojtek.Keys.MachineId, owner.Here(wojtek.Keys.MachineId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1))));
        var desk = await wojtek.Api.HereAsync(owner.Here(wojtek.Keys.MachineId, DateTimeOffset.UtcNow, now.AddHours(1)));
        Assert.True(desk.IsDesk);
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.OwnerAsync()).DeskName == "wojtek");
        Assert.Equal("wojtek", router.Admin.Owner().Desk);

        // An older claim is refused by the router, and a router whose owner key changed can't move the desk on daemons that pinned the old one.
        Assert.Throws<PoniExp.Server.RouterException>(() => router.Desk.SetHereAsync(ise.Keys.MachineId, owner.Here(ise.Keys.MachineId, now.AddMinutes(-1), now.AddHours(1)))
            .GetAwaiter().GetResult());
        router.Admin.SetOwner(stranger.Key.ToString());
        await router.Desk.SetHereAsync(ise.Keys.MachineId, stranger.Here(ise.Keys.MachineId, DateTimeOffset.UtcNow, now.AddHours(2)));
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.DiagnosticsAsync()).Any(d => d.Detail.Contains("refused a desk claim")));
        Assert.Equal("wojtek", (await ise.Api.OwnerAsync()).DeskName);
        Assert.Equal("wojtek", (await wojtek.Api.OwnerAsync()).DeskName);
    }

    [Fact]
    public async Task ARequestReachesTheDeskAndTheAgentGetsTheAnswer()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek) = (setup.Ise, setup.Wojtek);
        await setup.HereAsync(wojtek);

        var asking = ise.Api.AskAsync(new LocalApprovalRequest("claude", "Delete the build cache", "rm -rf ./cache", "Bash", "rm -rf ./cache"));
        var waiting = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault());
        Assert.Equal(("ise/claude", "Delete the build cache", "rm -rf ./cache"), (waiting.From, waiting.Summary, waiting.Command));
        Assert.Contains("isn't the desk", (await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.AnswerAsync(waiting.Id.ToString(), true))).Message);
        await wojtek.Api.AnswerAsync(waiting.Id.ToString()[..8], approve: true);
        var answered = await asking.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal((ApprovalRecord.Approved, "wojtek"), (answered.State, answered.AnsweredBy));

        var denied = ise.Api.AskAsync(new LocalApprovalRequest("claude", "Push to main", Tool: "Bash", Command: "git push"));
        var second = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault());
        await wojtek.Api.AnswerAsync(second.Id.ToString(), approve: false, reason: "not before review");
        Assert.Equal((ApprovalRecord.Denied, "not before review"), ((await denied.WaitAsync(TimeSpan.FromSeconds(15))).State, (await denied).Reason));
    }

    [Fact]
    public async Task AnAnswerFromAMachineThatIsntTheDeskIsRefused()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek, blinky) = (setup.Ise, setup.Wojtek, setup.Blinky);
        await setup.HereAsync(wojtek);
        var request = await ise.Api.RequestApprovalAsync(new LocalApprovalRequest("claude", "Format the disk"));

        blinky.Store.Transact(DateTimeOffset.UtcNow, tx => blinky.Daemon.ComposeApproval(tx, ise.Keys.MachineId, MessageType.ApprovalAnswer,
            JsonSerializer.Serialize(new ApprovalAnswerBody(request.Id, true), WireProtocol.Json)));
        await blinky.Daemon.Relay.FlushOutboxAsync();
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.DiagnosticsAsync()).Any(d => d.Detail.Contains("isn't the desk")));
        Assert.Equal(ApprovalRecord.Pending, (await ise.Api.WaitApprovalAsync(request.Id, TimeSpan.Zero)).State);
    }

    [Fact]
    public async Task WithNoDeskARequestWaitsAndThenFollowsTheDesk()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek, blinky) = (setup.Ise, setup.Wojtek, setup.Blinky);
        var request = await ise.Api.RequestApprovalAsync(new LocalApprovalRequest("claude", "Restart the service"));
        Assert.Null(request.Desk);

        await setup.HereAsync(wojtek);
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.DeskAsync()).Count == 1);
        await setup.HereAsync(blinky); // the owner walks over
        await RouterFixture.EventuallyAsync(async () => (await blinky.Api.DeskAsync()).Count == 1);
        Assert.Empty(await wojtek.Api.DeskAsync());
        Assert.Equal("blinky", (await ise.Api.WaitApprovalAsync(request.Id, TimeSpan.Zero)).Desk);

        await blinky.Api.AwayAsync(); // the desk steps down without the owner's key
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.OwnerAsync()).DeskMachine is null);
        Assert.Empty(await blinky.Api.DeskAsync());
        Assert.Contains("isn't the desk", (await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.AwayAsync())).Message);
    }

    [Fact]
    public async Task TheDesksOwnAgentsAskItDirectly()
    {
        await using var setup = await Setup.StartAsync();
        var wojtek = setup.Wojtek;
        await setup.HereAsync(wojtek);
        var asking = wojtek.Api.AskAsync(new LocalApprovalRequest("claude", "Install a package", Tool: "Bash", Command: "winget install jq"));
        var waiting = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault());
        Assert.Equal("wojtek/claude", waiting.From);
        await wojtek.Api.AnswerAsync(waiting.Id.ToString(), approve: true, always: true);
        Assert.Equal(ApprovalRecord.Approved, (await asking.WaitAsync(TimeSpan.FromSeconds(10))).State);
        Assert.Single(await wojtek.Api.ApprovalRulesAsync());
    }

    [Fact]
    public async Task AlwaysAllowCoversOnlyThatAgentToolAndCommand()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek) = (setup.Ise, setup.Wojtek);
        await setup.HereAsync(wojtek);
        var first = ise.Api.AskAsync(new LocalApprovalRequest("claude", "Run the tests", Tool: "Bash", Command: "dotnet test"));
        var waiting = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault());
        await wojtek.Api.AnswerAsync(waiting.Id.ToString(), approve: true, always: true);
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(15))).Always);

        var again = await ise.Api.RequestApprovalAsync(new LocalApprovalRequest("claude", "Run the tests again", Tool: "Bash", Command: "dotnet test"));
        Assert.Equal((ApprovalRecord.Approved, "rule"), (again.State, again.AnsweredBy));
        Assert.Equal(ApprovalRecord.Pending, (await ise.Api.RequestApprovalAsync(new LocalApprovalRequest("codex", "Run the tests", Tool: "Bash", Command: "dotnet test"))).State);
        Assert.Equal(ApprovalRecord.Pending, (await ise.Api.RequestApprovalAsync(new LocalApprovalRequest("claude", "Clean", Tool: "Bash", Command: "git clean -fdx"))).State);

        var rule = Assert.Single(await ise.Api.ApprovalRulesAsync());
        Assert.Equal(("claude", "Bash", "dotnet test"), (rule.Agent, rule.Tool, rule.Subject));
        await ise.Api.RemoveApprovalRuleAsync(rule.Id.ToString()[..8]);
        Assert.Equal(ApprovalRecord.Pending, (await ise.Api.RequestApprovalAsync(new LocalApprovalRequest("claude", "Run the tests", Tool: "Bash", Command: "dotnet test"))).State);
    }

    [Fact]
    public async Task AWithdrawnRequestShowsSoOnTheDesk()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek) = (setup.Ise, setup.Wojtek);
        await setup.HereAsync(wojtek);
        using var giveUp = new CancellationTokenSource();
        var asking = ise.Api.AskAsync(new LocalApprovalRequest("claude", "Long wait"), giveUp.Token);
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.DeskAsync()).Count == 1);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.DeskAsync()).Count == 0);
        Assert.Equal(ApprovalRecord.Withdrawn, wojtek.Store.GetApprovals(ApprovalRecord.In).Single().State);

        var overtaken = Guid.NewGuid(); // the withdrawal arrives before its request
        await ise.Api.WithdrawApprovalAsync(overtaken);
        Assert.Equal(ApprovalRecord.Withdrawn, (await ise.Api.RequestApprovalAsync(new LocalApprovalRequest("claude", "Too late", Id: overtaken))).State);
        await Task.Delay(300);
        Assert.Empty(await wojtek.Api.DeskAsync());
    }

    [Fact]
    public async Task ClaudeCodesPermissionPromptsGoToTheDesk()
    {
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek) = (setup.Ise, setup.Wojtek);
        await setup.HereAsync(wojtek);
        var tools = new PoniExp.Mcp.PoniExpTools(new PoniExp.Mcp.McpAgent(ise.Api, "claude"));
        var input = JsonDocument.Parse("""{"command":"npm publish","description":"Publish the package"}""").RootElement;

        var allowing = tools.ApproveAsync("Bash", input);
        var first = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault());
        Assert.Equal(("Bash: npm publish", "npm publish"), (first.Summary, first.Command));
        await wojtek.Api.AnswerAsync(first.Id.ToString(), approve: true);
        var allowed = JsonDocument.Parse(await allowing.WaitAsync(TimeSpan.FromSeconds(15))).RootElement;
        Assert.Equal("allow", allowed.GetProperty("behavior").GetString());
        Assert.Equal("npm publish", allowed.GetProperty("updatedInput").GetProperty("command").GetString());

        var denying = tools.ApproveAsync("Write", JsonDocument.Parse("""{"file_path":"C:/x.txt","content":"hi"}""").RootElement);
        var second = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault());
        await wojtek.Api.AnswerAsync(second.Id.ToString(), approve: false, reason: "wrong folder");
        var denied = JsonDocument.Parse(await denying.WaitAsync(TimeSpan.FromSeconds(15))).RootElement;
        Assert.Equal(("deny", "The owner denied it: wrong folder"), (denied.GetProperty("behavior").GetString(), denied.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Given_ARequestWaitingOnTheDesk_When_CompletingApproveWithTwoCharacters_Then_TheDaemonOffersItsFullId()
    {
        // Given
        await using var setup = await Setup.StartAsync();
        var (ise, wojtek) = (setup.Ise, setup.Wojtek);
        await setup.HereAsync(wojtek);
        _ = ise.Api.AskAsync(new LocalApprovalRequest("claude", "Restart the gateway", Tool: "Bash", Command: "hermes gateway restart"));
        var waiting = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.DeskAsync()).SingleOrDefault());

        // When
        var offered = await PoniExp.Client.Completion.CommandCompletion.CompleteAsync(["approve", waiting.Id.ToString()[..2]],
            new PoniExp.Client.Completion.LocalApiCompletionSource(wojtek.Api));

        // Then
        Assert.Equal([waiting.Id.ToString()], offered);
    }

    /// <summary>Three machines with the owner key set and pinned.</summary>
    private sealed class Setup : IAsyncDisposable
    {
        public required RouterFixture Router { get; init; }
        public required DaemonFixture Ise { get; init; }
        public required DaemonFixture Wojtek { get; init; }
        public required DaemonFixture Blinky { get; init; }
        public TestOwner Owner { get; } = TestOwner.Create();

        public static async Task<Setup> StartAsync()
        {
            var router = await RouterFixture.StartAsync();
            var setup = new Setup
            {
                Router = router,
                Ise = await DaemonFixture.StartAsync(router, "ise"),
                Wojtek = await DaemonFixture.StartAsync(router, "wojtek"),
                Blinky = await DaemonFixture.StartAsync(router, "blinky"),
            };
            router.Admin.SetOwner(setup.Owner.Key.ToString());
            foreach (var d in new[] { setup.Ise, setup.Wojtek, setup.Blinky })
            {
                await d.Api.PinOwnerAsync(setup.Owner.Key.ToString());
            }

            return setup;
        }

        public async Task HereAsync(DaemonFixture desk)
        {
            await Task.Delay(5); // a newer issue time than the last claim
            var now = DateTimeOffset.UtcNow;
            await desk.Api.HereAsync(Owner.Here(desk.Keys.MachineId, now, now.AddHours(12)));
            foreach (var d in new[] { Ise, Wojtek, Blinky })
            {
                await RouterFixture.EventuallyAsync(async () => (await d.Api.OwnerAsync()).DeskMachine == desk.Keys.MachineId);
            }
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
