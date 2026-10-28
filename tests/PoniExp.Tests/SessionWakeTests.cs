using System.Diagnostics;
using System.Text.Json.Nodes;
using PoniExp.Client;
using PoniExp.Mcp;

namespace PoniExp.Tests;

/// <summary>Per-session agents: an open Claude Code or Hermes session is its own agent, woken by its own messages.</summary>
public class SessionWakeTests
{
    private const string SessionId = "8B1D44E2-7c0f-4f6e-9a51-2d3c4b5a6978";
    private const string SessionAgent = "claude.8b1d44e2";

    [Fact]
    public void Given_AClaudeSessionId_When_ItsSessionAgentIsNamed_Then_ItIsTheBaseAndTheFirstEightLettersOrDigits()
    {
        // Given
        var sessionId = SessionId;

        // When
        var agent = SessionWake.SessionAgent("claude", sessionId);

        // Then
        Assert.Equal(SessionAgent, agent);
    }

    [Theory]
    [InlineData("20261028_085157_8fcb67c3", "hermes-default.8fcb67c3")]
    [InlineData("local-3f5ede2fe0fc1ef7ca799f5d5bde67907a09df35252cca684ac70fc4746a98d9", "hermes-default.3f5ede2f")]
    [InlineData("20261028_133348_ab12cd", "hermes-default.ab12cd")]
    [InlineData("local", "hermes-default.local")]
    public void Given_AHermesSessionKey_When_ItsSessionAgentIsNamed_Then_ItSkipsTheWordAndTimestampPrefixes(string sessionKey, string expected)
    {
        // Given
        var key = sessionKey;

        // When
        var agent = SessionWake.SessionAgent("hermes-default", key);

        // Then
        Assert.Equal(expected, agent);
    }

    [Theory]
    [InlineData(null, "claude")]
    [InlineData("", "claude")]
    [InlineData("claude", "claude")]
    [InlineData(" claude.8b1d44e2 ", "claude.8b1d44e2")]
    public void Given_McpToolsForClaude_When_AToolActsAsItselfOrOneOfItsSessions_Then_ThatAgentIsUsed(string? requested, string expected)
    {
        // Given
        var tools = new PoniExpTools(new McpAgent(null!, "claude"));

        // When
        var agent = tools.As(requested);

        // Then
        Assert.Equal(expected, agent);
    }

    [Theory]
    [InlineData("hermes-default")]
    [InlineData("claudex.8b1d44e2")]
    [InlineData("claude.")]
    [InlineData("hermes-default.claude.8b1d44e2")]
    public void Given_McpToolsForClaude_When_AToolAsksToActAsAnUnrelatedAgent_Then_ItIsRefused(string requested)
    {
        // Given
        var tools = new PoniExpTools(new McpAgent(null!, "claude"));

        // When
        var refused = Record.Exception(() => tools.As(requested));

        // Then
        Assert.IsType<ArgumentException>(refused);
    }

    [Fact]
    public async Task Given_AClaudeSessionStarting_When_TheHookAnnounces_Then_ItGivesTheSessionItsAddressAsContext()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var stdout = new StringWriter();

        // When
        var exit = await RunHookAsync(wojtek, "SessionStart", announce: true, stdout: stdout);

        // Then
        Assert.Equal(0, exit);
        var output = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        Assert.Equal("SessionStart", output["hookEventName"]!.GetValue<string>());
        Assert.Contains($"wojtek/{SessionAgent}", output["additionalContext"]!.GetValue<string>());
    }

    [Fact]
    public async Task Given_AWaitingHook_When_AMessageArrivesForItsSession_Then_ItWakesClaudeWithANudgeButNotTheBody()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var stderr = new StringWriter();
        var state = NewStateDirectory();
        var hook = RunHookAsync(wojtek, "Stop", stderr: stderr, state: state);
        await WaitOnlineAsync(wojtek, SessionAgent);

        // When
        await ise.Api.SendAsync(new LocalSendRequest("ops", [$"wojtek/{SessionAgent}"], "secret body"));

        // Then
        Assert.Equal(ClaudeHook.Wake, await hook.WaitAsync(TimeSpan.FromSeconds(20)));
        var nudge = stderr.ToString();
        Assert.Contains($"1 new message for wojtek/{SessionAgent} (this session) from ise/ops", nudge);
        Assert.Contains($"agent \"{SessionAgent}\"", nudge);
        Assert.DoesNotContain("secret", nudge);
    }

    [Fact]
    public async Task Given_AMessageTheSessionWasAlreadyNudgedAbout_When_TheHookWaitsAgain_Then_ItDoesNotWakeClaudeAgain()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var state = NewStateDirectory();
        var first = RunHookAsync(wojtek, "SessionStart", state: state);
        await WaitOnlineAsync(wojtek, SessionAgent);
        await ise.Api.SendAsync(new LocalSendRequest("ops", [$"wojtek/{SessionAgent}"], "unanswered"));
        Assert.Equal(ClaudeHook.Wake, await first.WaitAsync(TimeSpan.FromSeconds(20)));

        // When
        var again = await RunHookAsync(wojtek, "Stop", state: state, maxWait: TimeSpan.FromSeconds(3));

        // Then
        Assert.Equal(0, again);
    }

    [Fact]
    public async Task Given_AMessageNudgedAboutAndANewOne_When_TheHookWaitsAgain_Then_ItWakesClaudeForTheNewOneOnly()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var state = NewStateDirectory();
        var first = RunHookAsync(wojtek, "SessionStart", state: state);
        await WaitOnlineAsync(wojtek, SessionAgent);
        await ise.Api.SendAsync(new LocalSendRequest("ops", [$"wojtek/{SessionAgent}"], "old"));
        Assert.Equal(ClaudeHook.Wake, await first.WaitAsync(TimeSpan.FromSeconds(20)));
        await ise.Api.SendAsync(new LocalSendRequest("bot", [$"wojtek/{SessionAgent}"], "new"));
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync(SessionAgent)).Count == 2);
        var stderr = new StringWriter();

        // When
        var again = await RunHookAsync(wojtek, "Stop", stderr: stderr, state: state);

        // Then
        Assert.Equal(ClaudeHook.Wake, again);
        Assert.Contains("1 new message", stderr.ToString());
        Assert.Contains("from ise/bot", stderr.ToString());
        Assert.DoesNotContain("ise/ops", stderr.ToString());
    }

    [Fact]
    public async Task Given_AHookAlreadyWaitingForTheSession_When_AnotherHookStarts_Then_ItLeavesAtOnceWithoutWaking()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        using var stopFirst = new CancellationTokenSource();
        var first = RunHookAsync(wojtek, "SessionStart", state: NewStateDirectory(), ct: stopFirst.Token);
        await WaitOnlineAsync(wojtek, SessionAgent);
        var clock = Stopwatch.StartNew();

        // When
        var second = await RunHookAsync(wojtek, "Stop", state: NewStateDirectory(), maxWait: TimeSpan.FromSeconds(30));

        // Then
        Assert.Equal(0, second);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        await stopFirst.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task Given_ASessionAgent_When_TheSessionEnds_Then_ItIsRetired()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RunHookAsync(wojtek, "SessionStart", maxWait: TimeSpan.FromSeconds(1));
        Assert.Contains(await wojtek.Api.AgentsAsync(), a => a.Name == SessionAgent && a.RetiredOn is null);

        // When
        var exit = await RunHookAsync(wojtek, "SessionEnd");

        // Then
        Assert.Equal(0, exit);
        Assert.Contains(await wojtek.Api.AgentsAsync(), a => a.Name == SessionAgent && a.RetiredOn is not null);
    }

    [Fact]
    public async Task Given_AMessageForASessionAgent_When_TheMcpInboxToolActsAsThatSession_Then_OnlyThatSessionSeesIt()
    {
        // Given
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RunHookAsync(wojtek, "SessionStart", maxWait: TimeSpan.FromSeconds(1));
        await ise.Api.SendAsync(new LocalSendRequest("ops", [$"wojtek/{SessionAgent}"], "for the session"));
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync(SessionAgent)).Count == 1);
        var tools = new PoniExpTools(new McpAgent(wojtek.Api, "claude"));

        // When
        var session = await tools.InboxAsync(SessionAgent);
        var plain = await tools.InboxAsync();

        // Then
        Assert.Contains("for the session", session);
        Assert.DoesNotContain("for the session", plain);
    }

    private static Task<int> RunHookAsync(DaemonFixture machine, string eventName, bool announce = false, TextWriter? stdout = null,
        TextWriter? stderr = null, string? state = null, TimeSpan? maxWait = null, CancellationToken ct = default)
    {
        var input = new JsonObject { ["session_id"] = SessionId, ["hook_event_name"] = eventName }.ToJsonString();
        return ClaudeHook.RunAsync(machine.Api, machine.Name, "claude", input, announce, maxWait ?? TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(300), state ?? NewStateDirectory(), stdout ?? TextWriter.Null, stderr ?? TextWriter.Null, ct);
    }

    private static string NewStateDirectory() => Path.Combine(Path.GetTempPath(), "poniexp-hook-tests", Guid.NewGuid().ToString("N"));

    private static Task WaitOnlineAsync(DaemonFixture machine, string agent) =>
        RouterFixture.EventuallyAsync(async () => (await machine.Api.AgentsAsync()).Any(a => a.Name == agent && a.State == "online"));
}
