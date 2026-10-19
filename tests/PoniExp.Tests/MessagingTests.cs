using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

/// <summary>Phase 1: message format v2, the client database, expiry at both ends, duplicates and gaps.</summary>
public class MessagingTests
{
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task RepliesStayInTheConversationAndGoBackToTheSendingAgent()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, iseKey) = await router.MemberAsync("ise");
        var (wojtek, _) = await router.MemberAsync("wojtek");
        var request = Wait.For<ReceivedMessage>(h => wojtek.MessageReceived += m => { h(m); return Task.CompletedTask; });
        var reply = Wait.For<ReceivedMessage>(h => ise.MessageReceived += m => { h(m); return Task.CompletedTask; });

        var sent = await ise.SendAsync("wojtek/agent", new OutgoingMessage("sync please")
        {
            FromAgent = "cli",
            Subject = "sync",
            ContentType = "application/json",
            Metadata = new Dictionary<string, string> { ["priority"] = "high" },
        });
        var original = await request;
        Assert.Equal(("cli", "agent", "sync", "high"), (original.Content.FromAgent, original.Content.ToAgent, original.Content.Subject,
            original.Content.Metadata["priority"]));
        Assert.Equal(sent.Id, original.Content.OperationId); // the operation id defaults to the message id

        await wojtek.ReplyAsync(original, new OutgoingMessage("done") { Status = ReplyStatus.Done });
        var answer = await reply;

        Assert.Equal(iseKey.MachineId, answer.Envelope.ToMachine);
        Assert.Equal((sent.Id, original.Content.ConversationId), (answer.Content.ReplyTo!.Value, answer.Content.ConversationId));
        Assert.Equal(("agent", "cli", ReplyStatus.Done), (answer.Content.FromAgent, answer.Content.ToAgent, answer.Content.Status!.Value));
    }

    [Fact]
    public async Task AStatusIsOnlyForReplies()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");

        await Assert.ThrowsAsync<ArgumentException>(() => ise.SendAsync("ise", new OutgoingMessage("x") { Status = ReplyStatus.Done }));
        await Assert.ThrowsAsync<FormatException>(() => ise.SendAsync("ise/Bad Agent", new OutgoingMessage("x")));
    }

    [Fact]
    public async Task SendingToSeveralSendsSeparateMessages()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        var (blinky, blinkyKey) = await router.MemberAsync("blinky");
        var atWojtek = Wait.For<ReceivedMessage>(h => wojtek.MessageReceived += m => { h(m); return Task.CompletedTask; });
        var atBlinky = Wait.For<ReceivedMessage>(h => blinky.MessageReceived += m => { h(m); return Task.CompletedTask; });

        var sent = await ise.SendAsync(["wojtek", "blinky/agent"], new OutgoingMessage("hi both"));

        Assert.Equal(2, sent.Select(s => s.Id).Distinct().Count());
        Assert.All(sent, s => Assert.Equal(1, s.Sequence)); // each recipient machine has its own sequence
        var (w, b) = (await atWojtek, await atBlinky);
        Assert.Equal((wojtekKey.MachineId, "default"), (w.Envelope.ToMachine, w.Content.ToAgent));
        Assert.Equal((blinkyKey.MachineId, "agent"), (b.Envelope.ToMachine, b.Content.ToAgent));
        Assert.NotEqual(w.Content.ConversationId, b.Content.ConversationId); // not multi-tenant
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ise.SendAsync(["wojtek", "blinky"], new OutgoingMessage("x") { ConversationId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task AMachineCanMessageItsOwnAgents()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, _) = await router.MemberAsync("ise");
        var received = Wait.For<ReceivedMessage>(h => ise.MessageReceived += m => { h(m); return Task.CompletedTask; });

        await ise.SendAsync("ise/agent", new OutgoingMessage("note to self") { FromAgent = "cli" });

        Assert.Equal(("cli", "agent", "note to self"), ((await received).Content.FromAgent, (await received).Content.ToAgent,
            (await received).Content.Body));
    }

    [Fact]
    public async Task AnUnsetExpiryGetsTheDefaultAndALongOneIsCut()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, iseKey) = await router.MemberAsync("ise",
            TestSettings.Create(("Messaging:DefaultExpiry", "01:00:00"), ("Messaging:MaxExpiry", "02:00:00")));
        await router.MemberAsync("wojtek");

        var plain = await ise.SendAsync("wojtek", new OutgoingMessage("default"));
        var greedy = await ise.SendAsync("wojtek", new OutgoingMessage("forever") { ExpiresIn = TimeSpan.FromDays(30) });

        Assert.Equal(TimeSpan.FromHours(1), await LifetimeAsync(ise, iseKey, plain.Id));
        Assert.Equal(TimeSpan.FromHours(2), await LifetimeAsync(ise, iseKey, greedy.Id));
    }

    [Fact]
    public async Task TheRelayClampsAnExpiryBeyondItsMaximum()
    {
        await using var router = await RouterFixture.StartAsync(settings: new Dictionary<string, string?> { ["PoniExp:MaxExpiry"] = "01:00:00" });
        var (_, iseKey) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        await wojtek.DisposeAsync(); // offline, so the envelope stays queued where we can see it
        var connection = await RelayTests.RawMemberConnectionAsync(router, iseKey);
        var now = DateTimeOffset.UtcNow;

        var envelope = Envelopes.Sign(iseKey, wojtekKey, now, now.AddDays(30));
        await connection.InvokeAsync<SendResult>(HubMethods.Send, envelope);

        AssertNear(now.AddHours(1), router.Queue.ExpiryOf(envelope.Id)!.Value);
    }

    [Fact]
    public async Task TheRecipientClampsAnExpiryFromCreatedOnWhateverTheRouterDoes()
    {
        await using var router = await RouterFixture.StartAsync();
        var (_, iseKey) = await router.MemberAsync("ise");
        var settings = TestSettings.Create(("Messaging:MaxExpiry", "01:00:00"), ("Messaging:DefaultExpiry", "01:00:00"));
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek", settings);
        await wojtek.DisposeAsync();
        var created = Signatures.Truncate(DateTimeOffset.UtcNow.AddMinutes(-10));
        var envelope = Envelopes.Sign(iseKey, wojtekKey, created, created.AddDays(30), "long-lived");
        await router.Queue.EnqueueUncheckedAsync(envelope, DateTimeOffset.UtcNow.AddDays(30));

        var back = router.Client(wojtekKey, settings: settings);
        var received = Wait.For<ReceivedMessage>(h => back.MessageReceived += m => { h(m); return Task.CompletedTask; });
        await back.ConnectAsync();
        await received;

        Assert.Equal(created.AddHours(1), (await back.Store.GetMessageAsync(iseKey.MachineId, envelope.Id))!.ExpiresOn);
    }

    [Fact]
    public async Task SettingChangesApplyWithoutARestart()
    {
        await using var router = await RouterFixture.StartAsync(settings: new Dictionary<string, string?> { ["PoniExp:MaxExpiry"] = "01:00:00" });
        var clientSettings = TestSettings.Create(("Messaging:DefaultExpiry", "01:00:00"));
        var (ise, iseKey) = await router.MemberAsync("ise", clientSettings);
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        await wojtek.DisposeAsync();

        // The router's maximum.
        var connection = await RelayTests.RawMemberConnectionAsync(router, iseKey);
        router.SetSetting("PoniExp:MaxExpiry", "03:00:00");
        var now = DateTimeOffset.UtcNow;
        var envelope = Envelopes.Sign(iseKey, wojtekKey, now, now.AddDays(30));
        await connection.InvokeAsync<SendResult>(HubMethods.Send, envelope);
        AssertNear(now.AddHours(3), router.Queue.ExpiryOf(envelope.Id)!.Value);

        // The client's default.
        TestSettings.Set(clientSettings, "Messaging:DefaultExpiry", "02:00:00");
        var sent = await ise.SendAsync("wojtek", new OutgoingMessage("later"));
        Assert.Equal(TimeSpan.FromHours(2), await LifetimeAsync(ise, iseKey, sent.Id));
    }

    [Fact]
    public async Task InvalidSettingsStopStartup()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            RouterFixture.StartAsync(settings: new Dictionary<string, string?> { ["PoniExp:ClockSkew"] = "-00:01:00" }));

        await using var router = await RouterFixture.StartAsync();
        using var identity = MachineKeys.Create();
        Assert.Throws<OptionsValidationException>(() => router.Client(identity, settings: TestSettings.Create(("Messaging:ClockSkew", "-00:01:00"))));
        Assert.Throws<OptionsValidationException>(() => router.Client(identity,
            settings: TestSettings.Create(("Messaging:DefaultExpiry", "2.00:00:00"), ("Messaging:MaxExpiry", "1.00:00:00"))));
    }

    [Fact]
    public async Task TheRecipientRefusesExpiredAndFutureMessagesAMisbehavingRouterDelivers()
    {
        await using var router = await RouterFixture.StartAsync();
        var (_, iseKey) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        await wojtek.DisposeAsync();
        var now = DateTimeOffset.UtcNow;
        await router.Queue.EnqueueUncheckedAsync(Envelopes.Sign(iseKey, wojtekKey, now.AddDays(-2), now.AddDays(-1), "stale", 1),
            now.AddDays(1));
        await router.Queue.EnqueueUncheckedAsync(Envelopes.Sign(iseKey, wojtekKey, now.AddHours(1), now.AddHours(2), "early", 2),
            now.AddDays(1));

        var back = router.Client(wojtekKey);
        var handled = 0;
        back.MessageReceived += _ => { Interlocked.Increment(ref handled); return Task.CompletedTask; };
        await back.ConnectAsync();

        await RouterFixture.EventuallyAsync(async () =>
            await back.Store.GetMetricAsync(iseKey.MachineId, ClientMetrics.Expired) == 1
            && await back.Store.GetMetricAsync(iseKey.MachineId, ClientMetrics.FromTheFuture) == 1);
        await RouterFixture.Eventually(() => router.Queue.Count == 0); // refused for good, so confirmed
        Assert.Equal(0, handled);
    }

    [Fact]
    public async Task GapsAreCountedIncludingAfterARestartAndWhenAnExpiredMessageWasDropped()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, iseKey) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        var first = Wait.For<string>(h => wojtek.MessageReceived += m => { h(m.Content.Body); return Task.CompletedTask; });
        await ise.SendAsync("wojtek", new OutgoingMessage("one"));
        Assert.Equal("one", await first);
        await wojtek.DisposeAsync();
        await RouterFixture.Eventually(() => !ise.Machines.Single(m => m.Name == "wojtek").Online);

        await ise.SendAsync("wojtek", new OutgoingMessage("two") { ExpiresIn = TimeSpan.FromMilliseconds(300) });
        await Task.Delay(TimeSpan.FromMilliseconds(800)); // two expires on the router, which drops it
        await ise.SendAsync("wojtek", new OutgoingMessage("three"));

        var back = router.Client(wojtekKey); // a restart: same database, new process
        var bodies = new List<string>();
        var third = Wait.For<string>(h => back.MessageReceived += m =>
        {
            lock (bodies)
            {
                bodies.Add(m.Content.Body);
            }

            h(m.Content.Body);
            return Task.CompletedTask;
        });
        await back.ConnectAsync();

        Assert.Equal("three", await third);
        Assert.Equal(1, await back.Store.GetMetricAsync(iseKey.MachineId, ClientMetrics.SequenceGap));
        Assert.Equal(["three"], bodies);
    }

    [Fact]
    public async Task ARedeliveryAfterACrashBeforeConfirmingIsHandledOnce()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, iseKey) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        await wojtek.DisposeAsync();
        await RouterFixture.Eventually(() => !ise.Machines.Single(m => m.Name == "wojtek").Online);
        await ise.SendAsync("wojtek", new OutgoingMessage("once"));

        var handled = 0;
        var crashing = router.Client(wojtekKey, confirm: false); // stores, then "crashes" before confirming
        var stored = Wait.For<string>(h => crashing.MessageReceived += m => { Interlocked.Increment(ref handled); h(m.Content.Body); return Task.CompletedTask; });
        await crashing.ConnectAsync();
        Assert.Equal("once", await stored);
        await crashing.DisposeAsync();
        Assert.Equal(1, router.Queue.Count);

        var restarted = router.Client(wojtekKey);
        restarted.MessageReceived += _ => { Interlocked.Increment(ref handled); return Task.CompletedTask; };
        await restarted.ConnectAsync();

        await RouterFixture.Eventually(() => router.Queue.Count == 0);
        Assert.Equal(1, await restarted.Store.GetMetricAsync(iseKey.MachineId, ClientMetrics.Duplicate));
        Assert.Equal(1, handled);
    }

    [Fact]
    public async Task AReplayedMessageIsDroppedEvenAfterARestart()
    {
        await using var router = await RouterFixture.StartAsync();
        var (ise, iseKey) = await router.MemberAsync("ise");
        var (wojtek, wojtekKey) = await router.MemberAsync("wojtek");
        var received = Wait.For<Envelope>(h => wojtek.MessageReceived += m => { h(m.Envelope); return Task.CompletedTask; });
        await ise.SendAsync("wojtek", new OutgoingMessage("pay 10"));
        var envelope = await received;
        await RouterFixture.Eventually(() => router.Queue.Count == 0);
        await wojtek.DisposeAsync();

        await router.Queue.EnqueueUncheckedAsync(envelope, DateTimeOffset.UtcNow.AddDays(1)); // the router replays it
        var restarted = router.Client(wojtekKey);
        var handled = 0;
        restarted.MessageReceived += _ => { Interlocked.Increment(ref handled); return Task.CompletedTask; };
        await restarted.ConnectAsync();

        await RouterFixture.EventuallyAsync(async () => await restarted.Store.GetMetricAsync(iseKey.MachineId, ClientMetrics.Duplicate) == 1);
        await RouterFixture.Eventually(() => router.Queue.Count == 0);
        Assert.Equal(0, handled);
    }

    [Fact]
    public async Task AMismatchedProtocolVersionIsRefused()
    {
        await using var router = await RouterFixture.StartAsync();

        var old = await RelayTests.RawConnectionAsync(router, hello: false);
        Assert.Contains("upgrade poniexp-client",
            (await Assert.ThrowsAsync<HubException>(() => old.InvokeAsync<HelloResult>(HubMethods.Hello, WireProtocol.Version - 1))).Message);
        Assert.Contains("upgrade the relay",
            (await Assert.ThrowsAsync<HubException>(() => old.InvokeAsync<HelloResult>(HubMethods.Hello, WireProtocol.Version + 1))).Message);
        Assert.Contains("hello", (await Assert.ThrowsAsync<HubException>(() => old.InvokeAsync<string>(HubMethods.GetChallenge))).Message);
        Assert.Equal(WireProtocol.Version, (await old.InvokeAsync<HelloResult>(HubMethods.Hello, WireProtocol.Version)).ProtocolVersion);
    }

    private static async Task<TimeSpan> LifetimeAsync(RelayClient client, MachineKeys sender, Guid id)
    {
        var stored = (await client.Store.GetMessageAsync(sender.MachineId, id))!;
        return stored.Envelope!.ExpiresOn - stored.CreatedOn;
    }

    private static void AssertNear(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.InRange(actual, expected - Slack, expected + Slack);
}
