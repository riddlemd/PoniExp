using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Protocol;
using PoniExp.Daemon;
using PoniExp.Daemon.Service;
using PoniExp.Mcp;

namespace PoniExp.Tests;

/// <summary>Phase 3: the daemon, its local API, receipts, agent announcements, the outbox and the MCP tools.</summary>
public class DaemonTests
{
    // ---- receipts ----

    [Fact]
    public async Task ReceivedArrivesWhileTheAgentIsOfflineAndAcknowledgedOnlyAfterAnAck()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "vault"); // known, but offline

        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/vault"], "open the vault"))).Single();
        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Received);

        var inbox = await wojtek.Api.InboxAsync("vault");
        Assert.Equal("open the vault", Assert.Single(inbox).Body);
        await Task.Delay(500); // fetching isn't acknowledging
        Assert.Equal(MessageEvents.Received, ise.Store.GetState(ise.Keys.MachineId, sent.Id, MessageDirection.Out));
        Assert.Equal(MessageEvents.Fetched, (await wojtek.Api.InboxAsync("vault")).Single().State);

        await wojtek.Api.AckAsync("vault", sent.Id);
        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Acknowledged);
        Assert.Empty(await wojtek.Api.InboxAsync("vault"));
        var history = (await ise.Api.MessageAsync("ops", sent.Id)).Events.Select(e => e.Event).ToList();
        Assert.Equal([MessageEvents.Queued, MessageEvents.Relayed, MessageEvents.Received, MessageEvents.Acknowledged], history);
    }

    [Fact]
    public async Task ReplyingAcknowledgesAndBothSidesSeeTheConversation()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");

        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "how many?", Subject: "count"))).Single();
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 1);
        await wojtek.Api.ReplyAsync("agent", new LocalReplyRequest(sent.Id, "three", "done"));

        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Acknowledged);
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.InboxAsync("ops")).Count == 1);
        var reply = (await ise.Api.InboxAsync("ops")).Single();
        Assert.Equal(("three", "done", sent.Id, sent.ConversationId, "wojtek/agent"), (reply.Body, reply.Status, reply.ReplyTo, reply.ConversationId, reply.From));
        foreach (var (side, agent) in new[] { (ise, "ops"), (wojtek, "agent") })
        {
            var conversation = await side.Api.ConversationAsync(agent, sent.ConversationId);
            Assert.Equal(["how many?", "three"], conversation.Select(m => m.Body));
        }
    }

    [Fact]
    public async Task AMessageForAnUnknownOrRetiredAgentIsRejectedWithAReason()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");

        var unknown = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/nobody"], "hello?"))).Single();
        await ise.WaitForOutgoingAsync(unknown.Id, MessageEvents.Rejected);
        Assert.Contains("unknown agent 'nobody'", (await ise.Api.MessageAsync("ops", unknown.Id)).Message.StateDetail);
        Assert.Equal(MessageEvents.Rejected, wojtek.Store.GetState(ise.Keys.MachineId, unknown.Id, MessageDirection.In));

        await RegisterAndLeaveAsync(wojtek, "temp");
        var before = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/temp"], "still there?"))).Single();
        await ise.WaitForOutgoingAsync(before.Id, MessageEvents.Received);

        await wojtek.Api.RetireAsync("temp");
        var after = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/temp"], "and now?"))).Single();
        await ise.WaitForOutgoingAsync(after.Id, MessageEvents.Rejected);
        Assert.Equal("retired", (await wojtek.Api.AgentsAsync()).Single(e => e.Name == "temp").State);
    }

    [Fact]
    public async Task ADuplicateGetsNoSecondReceipt()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "once"))).Single();
        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Received);
        var envelope = (await ise.Store.GetMessageAsync(ise.Keys.MachineId, sent.Id))!.Envelope!;

        // The same envelope again, as a replaying router (or a resend after a lost confirmation) would deliver it.
        // Once the router no longer holds the original: it drops a resend of an id it still has.
        await RouterFixture.Eventually(() => router.Queue.ExpiryOf(sent.Id) is null);
        await using var raw = await RelayTests.RawMemberConnectionAsync(router, ise.Keys);
        await raw.InvokeAsync<SendResult>(HubMethods.Send, envelope);
        // At least once: under load the router may also redeliver it on a reconnect before the confirm lands.
        await RouterFixture.EventuallyAsync(async () => await wojtek.Store.GetMetricAsync(ise.Keys.MachineId, ClientMetrics.Duplicate) >= 1);

        await Task.Delay(300);
        Assert.Equal(1, wojtek.CountMessages("r.type = 'receipt' AND r.reply_to = $id", ("$id", sent.Id.ToString("D"))));
    }

    [Fact]
    public async Task NothingAutomaticIsSentInReplyToSomethingAutomatic()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(ise, "ops");
        await RegisterAndLeaveAsync(wojtek, "agent"); // announcements both ways
        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "go"))).Single();
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 1);
        await wojtek.Api.ReplyAsync("agent", new LocalReplyRequest(sent.Id, "went", "done"));
        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Acknowledged);
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.InboxAsync("ops")).Count == 1);
        await Task.Delay(500);

        foreach (var side in new[] { ise, wojtek })
        {
            Assert.True(side.CountMessages("r.type = 'agent'") > 0);
            Assert.True(side.CountMessages("r.type = 'receipt'") > 0);
            // Every receipt answers a message; nothing answers a receipt or an announcement.
            Assert.Equal(0, side.CountMessages("r.reply_to IN (SELECT id FROM messages WHERE type <> 'message')"));
            Assert.Equal(0, side.CountMessages("r.type = 'receipt' AND r.reply_to NOT IN (SELECT id FROM messages WHERE type = 'message')"));
        }
    }

    // ---- agent announcements ----

    [Fact]
    public async Task AnAgentsOnlineLeftAndTimedOutStatesReachAnotherMachine()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");

        var connection = new AgentConnection(ise.Api, "agent", _ => Task.CompletedTask);
        await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
        await PeerStateAsync(wojtek, "ise", "agent", "online");
        await connection.DisposeAsync();
        await PeerStateAsync(wojtek, "ise", "agent", "left");

        // Stops without leaving (the process died): timed out.
        using (var stopped = new CancellationTokenSource())
        {
            var stream = ise.Api.StreamAsync("agent", stopped.Token).GetAsyncEnumerator(stopped.Token);
            await stream.MoveNextAsync();
            await PeerStateAsync(wojtek, "ise", "agent", "online");
            await stopped.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
        }

        await PeerStateAsync(wojtek, "ise", "agent", "timed-out");

        // Connected but silent (no heartbeats): timed out after Daemon:AgentTimeout.
        var silent = ise.Api.StreamAsync("agent").GetAsyncEnumerator();
        await silent.MoveNextAsync();
        await PeerStateAsync(wojtek, "ise", "agent", "online");
        await PeerStateAsync(wojtek, "ise", "agent", "timed-out");
        Assert.False(await silent.MoveNextAsync()); // the daemon ended the stream
        await silent.DisposeAsync();
    }

    [Fact]
    public async Task AMachineThatWasOfflineCatchesUpWhenItReconnects()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        await blinky.StopAsync();

        await using var late = new AgentConnection(ise.Api, "late", _ => Task.CompletedTask);
        await late.Registered.WaitAsync(TimeSpan.FromSeconds(10));
        await blinky.StartAsync();

        await PeerStateAsync(blinky, "ise", "late", "online");
        await ise.StopAsync(); // a machine that goes away takes its agents with it
        await PeerStateAsync(blinky, "ise", "late", "offline");
    }

    [Fact]
    public async Task AReplayedOrLateAnnouncementIsIgnored()
    {
        var store = new ClientStore(Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N") + ".db"));
        var now = DateTimeOffset.UtcNow;
        AnnouncedAgent Agent(AgentState state) => new("agent", state, now);

        Assert.True(store.Transact(now, tx => tx.ApplyAnnouncement("ise", new AgentAnnouncement(5, [Agent(AgentState.Online)]))));
        Assert.False(store.Transact(now, tx => tx.ApplyAnnouncement("ise", new AgentAnnouncement(4, [Agent(AgentState.Left)]))));
        Assert.False(store.Transact(now, tx => tx.ApplyAnnouncement("ise", new AgentAnnouncement(5, [Agent(AgentState.Left)]))));
        Assert.Equal(AgentState.Online, store.GetPeerAgents().Single().State);
        Assert.True(store.Transact(now, tx => tx.ApplyAnnouncement("ise", new AgentAnnouncement(6, []))));
        Assert.Empty(store.GetPeerAgents()); // a newer list without it: retired
    }

    // ---- outbox ----

    [Fact]
    public async Task AMessageWhoseExpiryPassesInTheOutboxIsMarkedExpiredAndNeverRelayed()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, wojtekKeys) = await router.MemberAsync("wojtek");
        var target = await ise.ResolveAsync("wojtek");
        await wojtek.DisposeAsync();

        var envelope = ise.Store.Transact(DateTimeOffset.UtcNow, tx => ise.Compose(tx, target, "default",
            new OutgoingMessage("too late") { ExpiresIn = TimeSpan.FromMilliseconds(300) }));
        await Task.Delay(600);
        await ise.FlushOutboxAsync();

        Assert.Equal(MessageEvents.Expired, ise.Store.GetState(ise.MachineId, envelope.Id, MessageDirection.Out));
        Assert.Equal(0, router.Queue.Count);
        Assert.Empty(ise.Store.GetOutbox(ise.MachineId));
    }

    [Fact]
    public async Task MessagesSentWhileTheRouterIsDownWaitInTheOutbox()
    {
        var data = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        var router = await RouterFixture.StartAsync(dataDirectory: data, port: RouterFixture.RestartablePort());
        var url = router.Url;
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        await wojtek.Daemon.Relay.ResolveAsync("ise");
        await ise.Daemon.Relay.ResolveAsync("wojtek");
        await router.App.StopAsync(); // the router goes down (the daemons keep their machine lists)
        await RouterFixture.Eventually(() => !ise.Daemon.Relay.IsConnected);

        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "when you're back"))).Single();
        var outbox = ise.Store.GetOutbox(ise.Keys.MachineId);
        Assert.True(sent.State == "outbox", $"state {sent.State}, connected {ise.Daemon.Relay.IsConnected}");
        Assert.Contains(outbox, m => m.Id == sent.Id);

        await using var back = await RouterFixture.StartAsync(dataDirectory: data, port: url.Port);
        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Received); // only after it re-authenticated, so not refused
        await router.DisposeAsync();
    }

    // ---- delivery to agents ----

    [Fact]
    public async Task HeldMessagesAreDeliveredWhenTheAgentRegistersAndAgainUntilAcknowledged()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "while you were out"))).Single();
        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Received);
        await wojtek.RestartAsync(); // held across a daemon restart

        for (var round = 0; round < 2; round++)
        {
            var delivered = new TaskCompletionSource<LocalMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (new AgentConnection(wojtek.Api, "agent", e =>
                         {
                             if (e.Message is { } m)
                             {
                                 delivered.TrySetResult(m);
                             }

                             return Task.CompletedTask;
                         }))
            {
                Assert.Equal("while you were out", (await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Body);
            }
        }

        await wojtek.Api.AckAsync("agent", sent.Id);
        var after = new List<LocalMessage>();
        await using (var connection = new AgentConnection(wojtek.Api, "agent", e =>
                     {
                         if (e.Message is { } m)
                         {
                             after.Add(m);
                         }

                         return Task.CompletedTask;
                     }))
        {
            await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(300);
        }

        Assert.Empty(after);
    }

    [Fact]
    public async Task AReceiptReachesTheSendingAgentsStream()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var receipts = new List<LocalReceipt>();
        await using var ops = new AgentConnection(ise.Api, "ops", e =>
        {
            if (e.Receipt is { } r)
            {
                lock (receipts)
                {
                    receipts.Add(r);
                }
            }

            return Task.CompletedTask;
        });
        await ops.Registered.WaitAsync(TimeSpan.FromSeconds(10));

        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "hi"))).Single();
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 1);
        await wojtek.Api.AckAsync("agent", sent.Id);

        await RouterFixture.Eventually(() =>
        {
            lock (receipts)
            {
                return receipts.Select(r => r.Receipt).SequenceEqual(["received", "acknowledged"]);
            }
        });
        Assert.All(receipts, r => Assert.Equal((sent.Id, "wojtek/agent"), (r.MessageId, r.From)));
    }

    [Fact]
    public async Task AnAgentCanBeHeldByOneProcessAtATimeAndTheDaemonsAgentIsReserved()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var first = new AgentConnection(ise.Api, "agent", _ => Task.CompletedTask);
        await first.Registered.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await Assert.ThrowsAsync<LocalApiException>(async () => await ise.Api.StreamAsync("agent").GetAsyncEnumerator().MoveNextAsync());
        Assert.Equal(System.Net.HttpStatusCode.Conflict, second.Status);
        var reserved = await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.SendAsync(new LocalSendRequest("daemon", ["ise"], "x")));
        Assert.Contains("daemon's own agent", reserved.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DaemonHost.StartAsync(DaemonHost.Build(ise.Daemon.Context), ise.Daemon.Context));
    }

    // ---- MCP ----

    [Fact]
    public async Task TwoAgentsHoldAConversationThroughTheMcpTools()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "hermes-default");
        var asker = new PoniExpTools(new McpAgent(ise.Api, "claude"));
        var answerer = new PoniExpTools(new McpAgent(wojtek.Api, "hermes-default"));

        var sent = JsonSerializer.Deserialize<List<LocalSent>>(await asker.SendAsync(["wojtek/hermes-default"], "what's the build status?",
            subject: "build", metadata: new() { ["priority"] = "high" }), WireProtocol.Json)!.Single();
        await RouterFixture.EventuallyAsync(async () => Inbox(await answerer.InboxAsync()).Count == 1);
        var question = Inbox(await answerer.InboxAsync()).Single();
        Assert.Equal(("ise/claude", "build", "high"), (question.From, question.Subject, question.Metadata["priority"]));
        await answerer.AckAsync(question.Id.ToString());
        await answerer.ReplyAsync(question.Id.ToString(), "checking...", "progress");
        await answerer.ReplyAsync(question.Id.ToString(), "green", "done");

        await RouterFixture.EventuallyAsync(async () => Inbox(await asker.InboxAsync()).Count == 2);
        Assert.Equal(["checking...", "green"], Inbox(await asker.InboxAsync()).Select(m => m.Body));
        var conversation = Inbox(await asker.ConversationAsync(sent.ConversationId.ToString()));
        Assert.Equal(["what's the build status?", "checking...", "green"], conversation.Select(m => m.Body));
        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Acknowledged);
        Assert.Contains("hermes-default", await asker.MachinesAsync());
        Assert.Contains("\"connected\":true", await answerer.StatusAsync());

        static List<LocalMessage> Inbox(string json) => JsonSerializer.Deserialize<List<LocalMessage>>(json, WireProtocol.Json)!;
    }

    // ---- settings ----

    [Fact]
    public async Task ADaemonSettingChangeAppliesWithoutARestartAndABadOneStopsStartup()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise", new Dictionary<string, string?> { ["Daemon:AgentTimeout"] = "01:00:00" });
        var silent = ise.Api.StreamAsync("agent").GetAsyncEnumerator();
        await silent.MoveNextAsync();
        await Task.Delay(500);
        Assert.Equal("online", (await ise.Api.AgentsAsync()).Single().State);

        TestSettings.Set(ise.Configuration, "Daemon:AgentTimeout", "00:00:00.300");
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.AgentsAsync()).Single().State == "timed-out");
        await silent.DisposeAsync();

        var context = ise.Daemon.Context with { Address = LocalAddress.ForHome(ise.Home + "-other") };
        var bad = DaemonHost.Build(context, ["--Logging:LogLevel:Default=None"],
            c => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Daemon:AgentTimeout"] = "00:00:00", ["Daemon:SweepInterval"] = "00:00:01" }));
        await Assert.ThrowsAsync<OptionsValidationException>(() => bad.StartAsync());
        await bad.DisposeAsync();
    }

    // ---- service installation ----

    [Fact]
    public void ServicePlansMatchEachPlatform()
    {
        var windowsUser = ServiceInstaller.Install(ServiceMode.User, @"C:\Tools\poniexp-client.exe", @"C:\Users\m\.poniexp", os: OSPlatformKind.Windows);
        var run = string.Join(' ', windowsUser.Commands.Single());
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", run);
        Assert.Contains("--headless", run);
        Assert.Equal(["--headless", @"C:\Tools\poniexp-client.exe", "daemon", "--home", @"C:\Users\m\.poniexp"], windowsUser.StartDetached![1..]);
        Assert.False(windowsUser.NeedsAdmin);

        var windowsSystem = ServiceInstaller.Install(ServiceMode.System, @"C:\Tools\poniexp-client.exe", @"C:\Users\m\.poniexp", "S-1-5-21-1-2-3-1001",
            os: OSPlatformKind.Windows);
        Assert.True(windowsSystem.NeedsAdmin);
        Assert.Contains(windowsSystem.Commands, c => c.Contains(@"NT SERVICE\PoniExp") && c[1] == "create");
        Assert.Contains("S-1-5-21-1-2-3-1001", windowsSystem.Files.Single().Content);
        // The service account can't read the user's profile, so the app is copied out of it.
        Assert.Contains(windowsSystem.Copies, c => c.From == Path.GetDirectoryName(Path.GetFullPath(@"C:\Tools\poniexp-client.exe")));

        var mac = ServiceInstaller.Install(ServiceMode.User, "/opt/poniexp/poniexp-client", "/Users/m/.poniexp", userHome: "/Users/m", os: OSPlatformKind.MacOS);
        Assert.Contains("<string>daemon</string>", mac.Files.Single().Content);
        Assert.EndsWith("Library/LaunchAgents/com.poniexp.daemon.plist", mac.Files.Single().Path.Replace('\\', '/'));

        var linux = ServiceInstaller.Install(ServiceMode.System, "/opt/poniexp/poniexp-client", "/home/m/.poniexp", userHome: "/home/m", os: OSPlatformKind.Linux);
        Assert.Contains("ExecStart=/opt/poniexp/poniexp-client daemon --home /home/m/.poniexp", linux.Files.Single().Content);
        Assert.Equal("enable-linger", linux.Commands[0][1]);
        Assert.Equal("\"a b\" c \"d\\\"e\"", ServiceInstaller.Quote(["a b", "c", "d\"e"]));
    }

    [Fact]
    public void EachHomeHasItsOwnLocalAddress()
    {
        var a = LocalAddress.ForHome(Path.Combine(Path.GetTempPath(), "a"));
        Assert.Equal(a, LocalAddress.ForHome(Path.Combine(Path.GetTempPath(), "a") + Path.DirectorySeparatorChar));
        Assert.NotEqual(a, LocalAddress.ForHome(Path.Combine(Path.GetTempPath(), "b")));
    }

    // ---- helpers ----

    private static async Task RegisterAndLeaveAsync(DaemonFixture daemon, string agent)
    {
        await using var connection = new AgentConnection(daemon.Api, agent, _ => Task.CompletedTask);
        await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Task PeerStateAsync(DaemonFixture observer, string machine, string agent, string state) =>
        RouterFixture.EventuallyAsync(async () =>
            (await observer.Api.MachinesAsync()).SingleOrDefault(m => m.Name == machine)?.Agents.SingleOrDefault(e => e.Name == agent)?.State == state);
}
