using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server.Machines;

namespace PoniExp.Tests;

/// <summary>Phase 5: conversations, doing a request once, the log, retention, and the router's traffic log.</summary>
public class HistoryTests
{
    // ---- conversations and the log ----

    [Fact]
    public async Task ARestartedAgentGetsItsConversationsBack()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var first = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "count the sheep", Subject: "sheep"))).Single();
        var second = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "and the goats", Subject: "goats"))).Single();
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 2);
        await wojtek.Api.ReplyAsync("agent", new LocalReplyRequest(first.Id.ToString(), "forty", "done"));
        await wojtek.Api.ReplyAsync("agent", new LocalReplyRequest(second.Id.ToString(), "counting", "progress"));
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.InboxAsync("ops")).Count == 2);

        await wojtek.RestartAsync();

        var conversations = await wojtek.Api.ConversationsAsync("agent");
        Assert.Equal([("goats", "open", "ise/ops", "peer"), ("sheep", "finished", "ise/ops", "peer")],
            conversations.Select(c => (c.Subject, c.State, c.Peer, c.StartedBy)));
        var mine = await ise.Api.ConversationsAsync("ops");
        Assert.Equal(["local", "local"], mine.Select(c => c.StartedBy));
        Assert.Equal("finished", mine.Single(c => c.Id == first.ConversationId).State);
        Assert.Equal(["count the sheep", "forty"], (await wojtek.Api.ConversationAsync("agent", first.ConversationId)).Select(m => m.Body));
    }

    [Fact]
    public async Task TheLogFiltersAndShowsBodiesOnlyWhenAsked()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var sent = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "the secret plan", Subject: "plan"))).Single();
        await ise.WaitForOutgoingAsync(sent.Id, MessageEvents.Received);
        var other = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "unrelated"))).Single();
        await ise.WaitForOutgoingAsync(other.Id, MessageEvents.Received);

        var all = await ise.Api.LogAsync(new LocalLogQuery());
        Assert.Equal([sent.Id, other.Id], all.Select(e => e.Id)); // no receipts or announcements
        Assert.All(all, e => Assert.Null(e.Body));
        var entry = all[0];
        Assert.Equal(("ise/ops", "wojtek/agent", "out", "plan", MessageEvents.Received), (entry.From, entry.To, entry.Direction, entry.Subject, entry.State));

        var one = Assert.Single(await ise.Api.LogAsync(new LocalLogQuery(Conversation: sent.ConversationId, Bodies: true)));
        Assert.Equal("the secret plan", one.Body);
        Assert.Single(await wojtek.Api.LogAsync(new LocalLogQuery(Operation: sent.Id))); // operationId defaults to the message id
        Assert.Equal(2, (await wojtek.Api.LogAsync(new LocalLogQuery(Peer: "ise", Agent: "agent"))).Count);
        Assert.Empty(await wojtek.Api.LogAsync(new LocalLogQuery(Agent: "elsewhere")));
        Assert.Single(await wojtek.Api.LogAsync(new LocalLogQuery(Limit: 1))); // the most recent
        Assert.True((await ise.Api.LogAsync(new LocalLogQuery(Automatic: true))).Count > 2);
        Assert.Empty(await ise.Api.LogAsync(new LocalLogQuery(Since: DateTimeOffset.UtcNow.AddMinutes(1))));
    }

    // ---- doing a request once ----

    [Fact]
    public async Task ARetryWhileTheFirstIsRunningGetsStillRunningAndTheAgentSeesOnlyTheFirst()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var first = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "deploy"))).Single();
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 1);

        var retry = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "deploy", ConversationId: first.ConversationId,
            OperationId: first.Id))).Single();

        await ise.WaitForOutgoingAsync(retry.Id, MessageEvents.Acknowledged);
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.InboxAsync("ops")).Count == 1);
        var answer = (await ise.Api.InboxAsync("ops")).Single();
        Assert.Equal(("still running", "progress", retry.Id, first.Id, "wojtek/agent"),
            (answer.Body, answer.Status, answer.ReplyTo, answer.OperationId, answer.From));
        Assert.Equal([first.Id], (await wojtek.Api.InboxAsync("agent")).Select(m => m.Id));
        Assert.Contains(wojtek.Store.GetDiagnostics(), d => d.Detail.StartsWith($"retry of operation {first.Id}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARetryAfterTheFirstIsDoneGetsTheFinalReplyAgain()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var first = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "build it"))).Single();
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 1);
        await wojtek.Api.ReplyAsync("agent", new LocalReplyRequest(first.Id.ToString(), "built: v1.2", "done", Metadata: new Dictionary<string, string> { ["version"] = "1.2" }));
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.InboxAsync("ops")).Count == 1);
        await ise.Api.AckAsync("ops", (await ise.Api.InboxAsync("ops")).Single().Id);

        var retry = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "build it", OperationId: first.Id))).Single();

        await RouterFixture.EventuallyAsync(async () => (await ise.Api.InboxAsync("ops")).Count == 1);
        var copy = (await ise.Api.InboxAsync("ops")).Single();
        Assert.Equal(first.ConversationId, retry.ConversationId); // a retry stays in the first attempt's conversation
        Assert.Equal(("built: v1.2", "done", retry.Id, "1.2"), (copy.Body, copy.Status, copy.ReplyTo, copy.Metadata["version"]));
        Assert.Empty(await wojtek.Api.InboxAsync("agent"));
        Assert.Equal("done", Assert.Single(wojtek.Store.GetOperations(), o => o.OperationId == first.Id).State);
    }

    [Fact]
    public async Task ARetryAfterTheRecordExpiredIsANewRequest()
    {
        var shortWindow = new Dictionary<string, string?>
        {
            ["Messaging:MaxExpiry"] = "00:00:02",
            ["Messaging:DefaultExpiry"] = "00:00:02",
            ["Messaging:ClockSkew"] = "00:00:00.500",
        };
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek", shortWindow);
        await RegisterAndLeaveAsync(wojtek, "agent");
        var first = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "once", ExpiresIn: TimeSpan.FromSeconds(2)))).Single();
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 1);
        await wojtek.Api.AckAsync("agent", first.Id);

        await Task.Delay(TimeSpan.FromSeconds(3)); // past MaxExpiry plus skew: no retry of the first can be told apart any more
        var late = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "once", OperationId: first.Id))).Single();

        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Any(m => m.Id == late.Id));
        Assert.Equal(late.Id, Assert.Single(wojtek.Store.GetOperations(), o => o.OperationId == first.Id).FirstMessageId);
    }

    // ---- retention ----

    [Fact]
    public async Task RetentionDropsOldEnvelopesThenOldMetadataButNeverALiveMessage()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await RegisterAndLeaveAsync(wojtek, "agent");
        var done = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "handled"))).Single();
        var waiting = (await ise.Api.SendAsync(new LocalSendRequest("ops", ["wojtek/agent"], "still waiting"))).Single();
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 2);
        await wojtek.Api.AckAsync("agent", done.Id);
        var me = ise.Keys.MachineId;
        wojtek.Store.Transact(DateTimeOffset.UtcNow, tx => tx.Diagnose("info", "test", "an old problem"));
        var retention = new RetentionOptions();

        var later = wojtek.Store.ApplyRetention(retention, DateTimeOffset.UtcNow + TimeSpan.FromDays(100));
        Assert.True(later.EnvelopesDropped >= 1);
        Assert.Null((await wojtek.Store.GetMessageAsync(me, done.Id))!.Envelope); // the metadata stays
        Assert.NotNull((await wojtek.Store.GetMessageAsync(me, waiting.Id))!.Envelope); // live
        Assert.DoesNotContain(wojtek.Store.GetDiagnostics(), d => d.Detail == "an old problem");
        Assert.Empty(wojtek.Store.GetOperations()); // expired after MaxExpiry plus skew
        var bodyless = (await wojtek.Api.LogAsync(new LocalLogQuery(Bodies: true))).Single(e => e.Id == done.Id);
        Assert.True(bodyless.BodyDropped);
        Assert.Null(bodyless.Body);

        var much = wojtek.Store.ApplyRetention(retention, DateTimeOffset.UtcNow + TimeSpan.FromDays(400));
        Assert.True(much.MessagesDeleted >= 1);
        Assert.Null(await wojtek.Store.GetMessageAsync(me, done.Id));
        Assert.Empty(wojtek.Store.GetEvents(me, done.Id));
        Assert.NotNull(await wojtek.Store.GetMessageAsync(me, waiting.Id));
        Assert.Equal(MessageEvents.Fetched, wojtek.Store.GetState(me, waiting.Id, MessageDirection.In));
        Assert.DoesNotContain(wojtek.Store.GetConversations(), c => c.Id == done.ConversationId);
    }

    [Fact]
    public void TheSizeCapDropsTheOldestEnvelopesFirst()
    {
        var path = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N") + ".db");
        var store = new ClientStore(path);
        using var sender = MachineKeys.Create();
        using var me = MachineKeys.Create();
        var start = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        var body = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48 * 1024)); // ~64 KB, incompressible
        for (var i = 0; i < 40; i++)
        {
            var on = start.AddMinutes(i);
            var envelope = Envelopes.Sign(sender, me, on, on.AddDays(1), body, sequence: i + 1);
            store.Transact(on, tx =>
            {
                tx.InsertIncoming(envelope, Envelopes.Content(on, body), on, on.AddDays(1), me.MachineId);
                tx.AddEvent(sender.MachineId, envelope.Id, MessageDirection.In, "default", MessageEvents.Acknowledged);
            });
            ids.Add(envelope.Id);
        }

        Assert.True(store.UsedBytes() > 2 << 20, $"used {store.UsedBytes()}, file {new FileInfo(path).Length}, wal {(File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0)}");
        var result = store.ApplyRetention(new RetentionOptions { MaxDatabaseBytes = 2 << 20 }, start.AddHours(1));

        Assert.True(store.UsedBytes() <= 2 << 20);
        Assert.True(result.EnvelopesDropped > 0);
        var kept = ids.Select(id => store.GetMessageAsync(sender.MachineId, id).Result!.Envelope is not null).ToList();
        Assert.Equal(kept.OrderBy(k => k), kept); // dropped ones first, kept ones last
        Assert.True(kept[^1], $"kept {kept.Count(k => k)} of {kept.Count}, dropped {result.EnvelopesDropped}, used {store.UsedBytes()}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
        {
            File.Delete(f);
        }
    }

    [Fact]
    public async Task RetentionThatWouldBreakDuplicateDetectionRefusesToStart()
    {
        await using var router = await RouterFixture.StartAsync();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => DaemonFixture.StartAsync(router, "ise",
            new Dictionary<string, string?> { ["Retention:Metadata"] = "7.00:00:00" }));
        Assert.Contains("MaxExpiry plus ClockSkew", error.Message);
        await Assert.ThrowsAsync<OptionsValidationException>(() => DaemonFixture.StartAsync(router, "wojtek",
            new Dictionary<string, string?> { ["Retention:Envelopes"] = "400.00:00:00" }));
    }

    // ---- the router's traffic log ----

    [Fact]
    public async Task TheTrafficLogRecordsEachEventOnlyWhileItsOn()
    {
        await using var router = await RouterFixture.StartAsync();
        var log = router.App.Services.GetRequiredService<TrafficLog>();
        var (ise, iseKeys) = await router.MemberAsync("ise");
        var (wojtek, wojtekKeys) = await router.MemberAsync("wojtek");
        var unseen = await ise.SendAsync("wojtek", new OutgoingMessage("before the log"));
        await RouterFixture.Eventually(() => router.Queue.Count == 0);
        Assert.Empty(log.Read(null, null, 100));

        log.SetEnabled(true);
        var sent = await ise.SendAsync("wojtek", new OutgoingMessage("logged"));
        await RouterFixture.Eventually(() => log.Read(null, null, 100).Any(e => e.Event == TrafficEvents.Confirmed));
        var forged = Envelopes.Resign(Envelopes.Sign(iseKeys, wojtekKeys, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), wojtekKeys)
            with { FromMachine = iseKeys.MachineId };
        await using (var raw = await RelayTests.RawMemberConnectionAsync(router, iseKeys))
        {
            await Assert.ThrowsAsync<HubException>(() => raw.InvokeAsync<SendResult>(HubMethods.Send, forged));
        }

        var entries = log.Read(null, null, 100);
        Assert.Equal([TrafficEvents.Relayed, TrafficEvents.Delivered, TrafficEvents.Confirmed],
            entries.Where(e => e.MessageId == sent.Id).Select(e => e.Event));
        var relayed = entries.First(e => e.MessageId == sent.Id);
        Assert.Equal((iseKeys.MachineId, wojtekKeys.MachineId), (relayed.FromMachine, relayed.ToMachine));
        Assert.True(relayed.Size > 0);
        Assert.NotNull(relayed.Sequence);
        var refused = Assert.Single(entries, e => e.Event == TrafficEvents.Refused);
        Assert.Contains("signature", refused.Detail);
        Assert.Equal("127.0.0.1", refused.Address);
        Assert.DoesNotContain(entries, e => e.MessageId == unseen.Id);

        log.SetEnabled(false);
        await ise.SendAsync("wojtek", new OutgoingMessage("after"));
        await RouterFixture.Eventually(() => router.Queue.Count == 0);
        Assert.Equal(entries.Count, log.Read(null, null, 100).Count);

        router.SetSetting("PoniExp:TrafficLogRetention", "00:00:00.001");
        await Task.Delay(50);
        Assert.Equal(entries.Count, log.Prune());
        Assert.Empty(log.Read(null, null, 100));
        await ise.DisposeAsync();
        await wojtek.DisposeAsync();
    }

    [Fact]
    public async Task TheTrafficLogSwitchSurvivesARestartAndRecordsExpiryAndJoins()
    {
        var data = Path.Combine(Path.GetTempPath(), "poniexp-tests", Guid.NewGuid().ToString("N"));
        await using (var first = await RouterFixture.StartAsync(dataDirectory: data))
        {
            first.App.Services.GetRequiredService<TrafficLog>().SetEnabled(true);
            first.KeepData = true;
        }

        await using var router = await RouterFixture.StartAsync(dataDirectory: data, settings: new Dictionary<string, string?>
        {
            ["PoniExp:MaxExpiry"] = "00:00:01",
            ["PoniExp:ClockSkew"] = "00:00:00.100",
            ["PoniExp:MaintenanceInterval"] = "00:00:00.200",
        });
        var log = router.App.Services.GetRequiredService<TrafficLog>();
        Assert.True(log.Enabled);
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, _) = await router.MemberAsync("wojtek");
        await wojtek.DisposeAsync();
        var sent = await ise.SendAsync("wojtek", new OutgoingMessage("nobody's home"));

        await RouterFixture.Eventually(() => log.Read(null, null, 100).Any(e => e.MessageId == sent.Id && e.Event == TrafficEvents.Expired));
        Assert.Equal(2, log.Read(null, null, 100).Count(e => e.Event == TrafficEvents.JoinRequested));
        await ise.DisposeAsync();
    }

    private static async Task RegisterAndLeaveAsync(DaemonFixture daemon, string agent)
    {
        await using var connection = new AgentConnection(daemon.Api, agent, _ => Task.CompletedTask);
        await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
