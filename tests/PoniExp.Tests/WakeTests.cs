using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using PoniExp.Client;
using PoniExp.Daemon;

namespace PoniExp.Tests;

/// <summary>Phase 12: the wake hook, a doorbell for agents that only use MCP (Hermes).</summary>
public class WakeTests
{
    [Fact]
    public void Given_NewMessages_When_TheDaemonWritesTheNudge_Then_ItStartsWithTheTagMemoryProvidersSkip()
    {
        // Given
        IReadOnlyList<string> from = ["ise/ops"];

        // When
        var nudge = PoniExp.Daemon.Daemon.NudgeText("hermes", 1, from, test: false);

        // Then
        Assert.StartsWith("[ASYNC COMPLETE — PoniExp wake] 1 new message for agent 'hermes' from ise/ops", nudge);
    }

    [Fact]
    public void Given_AWakeTest_When_TheDaemonWritesTheNudge_Then_ItStartsWithTheTagMemoryProvidersSkip()
    {
        // Given
        IReadOnlyList<string> from = [];

        // When
        var nudge = PoniExp.Daemon.Daemon.NudgeText("hermes", 0, from, test: true);

        // Then
        Assert.StartsWith("[ASYNC COMPLETE — PoniExp wake] a test nudge for agent 'hermes'", nudge);
    }

    [Fact]
    public async Task ABurstOfMessagesWakesTheAgentOnceWithoutTheBodies()
    {
        await using var gateway = await FakeA2aGateway.StartAsync();
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek", new Dictionary<string, string?>
        {
            ["Wake:Delay"] = "00:00:00.300",
            ["Wake:Agents:hermes:A2aUrl"] = gateway.Url,
        });
        await Register(wojtek, "hermes");

        foreach (var body in new[] { "secret one", "secret two", "secret three" })
        {
            await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/hermes"], body));
        }

        await RouterFixture.Eventually(() => gateway.Received.Count == 1);
        var nudge = gateway.Received.Single();
        Assert.Contains("3 new messages for agent 'hermes' from ise/ops", nudge.Text);
        Assert.Contains("`inbox`", nudge.Text);
        Assert.DoesNotContain("secret", nudge.Text);
        Assert.Null(nudge.Authorization);
        Assert.Equal("message/stream", nudge.Method);
        await RouterFixture.Eventually(() => gateway.Completed == 1); // the stream was read to its end, not cut off

        await Task.Delay(1000);
        Assert.Single(gateway.Received); // nothing new: no second nudge

        await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/hermes"], "secret four"));
        await RouterFixture.Eventually(() => gateway.Received.Count == 2);
        Assert.Contains("1 new message for agent 'hermes'", gateway.Received.Last().Text);
        Assert.NotEqual(gateway.Received.First().ContextId, gateway.Received.Last().ContextId); // a fresh context each time

        await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/other"], "not for hermes"));
        await Task.Delay(800);
        Assert.Equal(2, gateway.Received.Count);
    }

    [Fact]
    public async Task AFailedNudgeIsRetriedAndTheTokenGoesAsBearer()
    {
        await using var gateway = await FakeA2aGateway.StartAsync();
        gateway.Mode = GatewayMode.Unauthorized;
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek", new Dictionary<string, string?>
        {
            ["Wake:Delay"] = "00:00:00.100",
            ["Wake:Retry"] = "00:00:00.500",
        });
        await Register(wojtek, "hermes");
        var set = await wojtek.Api.SetWakeAsync("hermes", new LocalWakeSet(gateway.Url, null, null, "s3cret"));
        Assert.True(set.HasToken);
        Assert.Contains(gateway.Url, await File.ReadAllTextAsync(Path.Combine(wojtek.Home, "settings.json")));
        Assert.DoesNotContain("s3cret", await File.ReadAllTextAsync(Path.Combine(wojtek.Home, "settings.json")));

        await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/hermes"], "wake up"));
        var failed = await RouterFixture.EventuallyValueAsync(async () => (await wojtek.Api.WakeTargetsAsync()).SingleOrDefault(t => t.LastError is not null));
        Assert.Contains("401", failed.LastError);

        gateway.Mode = GatewayMode.Ok;
        await RouterFixture.Eventually(() => gateway.Received.Any(r => r.Authorization == "Bearer s3cret"));
        var ok = (await wojtek.Api.WakeTargetsAsync()).Single();
        Assert.Null(ok.LastError);
        Assert.NotNull(ok.LastNudge);
        Assert.Equal(1, ok.Waiting);

        gateway.Mode = GatewayMode.Rejected;
        var refused = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.TestWakeAsync("hermes"));
        Assert.Contains("rejected: Anti-loop protection", refused.Message);

        await wojtek.Api.RemoveWakeAsync("hermes");
        Assert.Empty(await wojtek.Api.WakeTargetsAsync());
        Assert.Null(wojtek.Daemon.Secrets?.Load(PoniExp.Daemon.Daemon.WakeTokenName("hermes")));
    }

    [Fact]
    public async Task ACommandTargetGetsTheNudgeOnStdinAndItsExitCodeCounts()
    {
        var dir = Directory.CreateTempSubdirectory("poniexp-wake-").FullName;
        var output = Path.Combine(dir, "nudge.txt");
        var env = Path.Combine(dir, "env.txt");
        string[] command = OperatingSystem.IsWindows()
            ? ["cmd.exe", "/c", $"more > {output} & set PONIEXP_ > {env}"]
            : ["/bin/sh", "-c", $"cat > {output}; env | grep PONIEXP_ > {env}"];
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek", new Dictionary<string, string?> { ["Wake:Delay"] = "00:00:00.100" });
        await Register(wojtek, "claude");
        await wojtek.Api.SetWakeAsync("claude", new LocalWakeSet(null, command[0], [.. command.Skip(1)], null));

        await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/claude"], "hello"));
        await RouterFixture.Eventually(() => File.Exists(env) && File.ReadAllText(env).Contains("PONIEXP_FROM"));
        Assert.Contains("1 new message for agent 'claude' from ise/ops", await File.ReadAllTextAsync(output));
        var vars = await File.ReadAllTextAsync(env);
        Assert.Contains("PONIEXP_AGENT=claude", vars);
        Assert.Contains("PONIEXP_COUNT=1", vars);
        Assert.Contains("PONIEXP_FROM=ise/ops", vars);

        string[] failing = OperatingSystem.IsWindows() ? ["cmd.exe", "/c", "exit 3"] : ["/bin/sh", "-c", "exit 3"];
        await wojtek.Api.SetWakeAsync("claude", new LocalWakeSet(null, failing[0], [.. failing.Skip(1)], null));
        var refused = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.TestWakeAsync("claude"));
        Assert.Contains("exited with 3", refused.Message);
    }

    [Fact]
    public async Task WakeSettingsAreChecked()
    {
        var validator = new WakeOptionsValidator();
        WakeOptions With(string agent, WakeTarget target) => new() { Agents = { [agent] = target } };
        Assert.True(validator.Validate(null, With("hermes", new WakeTarget { A2aUrl = "http://127.0.0.1:9900" })).Succeeded);
        Assert.True(validator.Validate(null, With("hermes", new WakeTarget { A2aUrl = "https://wojtek.lan:9900" })).Succeeded);
        Assert.True(validator.Validate(null, With("claude", new WakeTarget { Command = "wake.sh" })).Succeeded);
        Assert.Contains("only to this machine", validator.Validate(null, With("hermes", new WakeTarget { A2aUrl = "http://192.168.50.138:9900" })).FailureMessage);
        Assert.Contains("exactly one", validator.Validate(null, With("hermes", new WakeTarget { A2aUrl = "http://localhost:1", Command = "x" })).FailureMessage);
        Assert.Contains("exactly one", validator.Validate(null, With("hermes", new WakeTarget())).FailureMessage);
        Assert.Contains("isn't an agent name", validator.Validate(null, With("Not An Agent!", new WakeTarget { Command = "x" })).FailureMessage);
        Assert.False(validator.Validate(null, new WakeOptions { Retry = TimeSpan.Zero }).Succeeded);

        await using var router = await RouterFixture.StartAsync();
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var refused = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.SetWakeAsync("hermes", new LocalWakeSet("http://10.0.0.5:9900", null, null, null)));
        Assert.Contains("use https", refused.Message);
        Assert.Contains("only for an A2A target",
            (await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.SetWakeAsync("claude", new LocalWakeSet(null, "x", null, "token")))).Message);
        Assert.Contains("no wake target", (await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.TestWakeAsync("nobody"))).Message);
    }

    [Fact]
    public void A2aEventsAreReadInEachShape()
    {
        Assert.Equal(("submitted", null), PoniExp.Daemon.Daemon.ParseA2aEvent("""{"jsonrpc":"2.0","id":"1","result":{"task":{"id":"t","status":{"state":"TASK_STATE_SUBMITTED"}}}}"""));
        Assert.Equal(("input-required", "which one?"), PoniExp.Daemon.Daemon.ParseA2aEvent(
            """{"result":{"statusUpdate":{"taskId":"t","status":{"state":"TASK_STATE_INPUT_REQUIRED","message":{"parts":[{"text":"which one?"}]}}}}}"""));
        Assert.Equal(("working", null), PoniExp.Daemon.Daemon.ParseA2aEvent("""{"kind":"status-update","status":{"state":"working"}}"""));
        Assert.Equal(("error", "rate limit exceeded"), PoniExp.Daemon.Daemon.ParseA2aEvent("""{"jsonrpc":"2.0","id":"1","error":{"code":-32029,"message":"rate limit exceeded"}}"""));
        Assert.Equal((null, null), PoniExp.Daemon.Daemon.ParseA2aEvent("not json"));
    }

    private static async Task Register(DaemonFixture daemon, string agent)
    {
        await using var connection = new AgentConnection(daemon.Api, agent, _ => Task.CompletedTask);
        await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

public enum GatewayMode { Ok, Unauthorized, Rejected }

/// <summary>Enough of a Hermes A2A gateway: <c>message/stream</c> answered with submitted, working, then completed.</summary>
public sealed class FakeA2aGateway : IAsyncDisposable
{
    private WebApplication _app = null!;

    public sealed record Nudge(string Method, string Text, string? ContextId, string? Authorization);

    public ConcurrentQueue<Nudge> Received { get; } = new();

    public volatile GatewayMode Mode;

    private int _completed;

    public int Completed => Volatile.Read(ref _completed);

    public string Url { get; private set; } = "";

    public static async Task<FakeA2aGateway> StartAsync()
    {
        var gateway = new FakeA2aGateway();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        gateway._app = builder.Build();
        gateway._app.MapPost("/", gateway.HandleAsync);
        await gateway._app.StartAsync();
        gateway.Url = gateway._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
        return gateway;
    }

    private async Task HandleAsync(HttpContext http)
    {
        if (Mode == GatewayMode.Unauthorized)
        {
            http.Response.StatusCode = 401;
            await http.Response.WriteAsync("""{"jsonrpc":"2.0","id":null,"error":{"code":-32001,"message":"unauthorized"}}""");
            return;
        }

        var request = (await JsonNode.ParseAsync(http.Request.Body))!;
        var message = request["params"]!["message"]!;
        var context = message["contextId"]?.GetValue<string>();
        Received.Enqueue(new Nudge(request["method"]!.GetValue<string>(), message["parts"]![0]!["text"]!.GetValue<string>(), context,
            http.Request.Headers.Authorization.FirstOrDefault()));
        var id = request["id"]!.ToJsonString();
        http.Response.ContentType = "text/event-stream";
        async Task Send(string result)
        {
            await http.Response.WriteAsync("data: {\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + result + "}\n\n");
            await http.Response.Body.FlushAsync();
        }

        string Status(string state, string? text = null)
        {
            var status = new JsonObject { ["state"] = state };
            if (text is not null)
            {
                status["message"] = new JsonObject { ["role"] = "ROLE_AGENT", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) };
            }

            return new JsonObject { ["statusUpdate"] = new JsonObject { ["taskId"] = "t", ["contextId"] = context, ["status"] = status } }.ToJsonString();
        }

        if (Mode == GatewayMode.Rejected)
        {
            await Send(Status("TASK_STATE_REJECTED", "Anti-loop protection"));
            return;
        }

        await Send(new JsonObject { ["task"] = new JsonObject { ["id"] = "t", ["contextId"] = context, ["status"] = new JsonObject { ["state"] = "TASK_STATE_SUBMITTED" } } }.ToJsonString());
        await Send(Status("TASK_STATE_WORKING"));
        await Task.Delay(300); // the agent's turn
        await Send(Status("TASK_STATE_COMPLETED"));
        await http.Response.WriteAsync(": done\n\n");
        Interlocked.Increment(ref _completed);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
