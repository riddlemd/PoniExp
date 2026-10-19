using System.Text.Json;
using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

/// <summary>Phase 7: roles, groups and rooms.</summary>
public class RoomTests
{
    // ---- roles ----

    [Fact]
    public async Task ARoleReachesAnyOneOnlineHolderOrEveryHolder()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        await using var online = new AgentConnection(wojtek.Api, "rev", _ => Task.CompletedTask, roles: ["reviewer", "gpu"]);
        await online.Registered.WaitAsync(TimeSpan.FromSeconds(10));
        await RegisterAndLeaveAsync(blinky, "rev2", ["reviewer"]); // a holder, but offline
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.MachinesAsync()).SelectMany(m => m.Agents)
            .Count(e => e.Roles?.Contains("reviewer") == true) == 2);

        var one = Assert.Single(await ise.Api.SendAsync(new LocalSendRequest("ops", ["role:reviewer"], "review this")));
        Assert.Equal("wojtek/rev", one.To); // the online one
        var all = await ise.Api.SendAsync(new LocalSendRequest("ops", ["role:reviewer/all"], "everyone review"));
        Assert.Equal(["blinky/rev2", "wojtek/rev"], all.Select(s => s.To).Order());
        var missing = await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.SendAsync(new LocalSendRequest("ops", ["role:nobody"], "?")));
        Assert.Contains("no agent declares the role 'nobody'", missing.Message);
    }

    // ---- groups ----

    [Fact]
    public async Task AGroupIsResolvedIntoSeparateMessages()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        await RegisterAndLeaveAsync(wojtek, "agent");
        await RegisterAndLeaveAsync(blinky, "bot", ["gpu"]);
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.MachinesAsync()).SelectMany(m => m.Agents).Any(e => e.Roles?.Contains("gpu") == true));
        router.Admin.SetGroup("team", ["wojtek/agent", "role:gpu/all"]);
        Assert.Throws<PoniExp.Server.RouterException>(() => router.Admin.SetGroup("nested", ["group:team"]));
        Assert.Throws<PoniExp.Server.RouterException>(() => router.Admin.SetGroup("ghost", ["nobody/agent"]));

        var sent = await ise.Api.SendAsync(new LocalSendRequest("ops", ["group:team"], "standup in 5"));

        Assert.Equal(["blinky/bot", "wojtek/agent"], sent.Select(s => s.To).Order());
        Assert.Equal(2, sent.Select(s => s.ConversationId).Distinct().Count()); // separate conversations: a group isn't a room
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.InboxAsync("agent")).Count == 1);
        Assert.DoesNotContain("blinky", (await wojtek.Api.InboxAsync("agent")).Single().To);
    }

    // ---- rooms ----

    [Fact]
    public async Task ARoomNameIsUniqueWhileLiveAndFreeOnceItCollapses()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");

        var first = await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "standup"));
        Assert.Equal(["ise/ops"], first.Members);
        var taken = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.CreateRoomAsync(new LocalRoomCreate("agent", "standup")));
        Assert.Contains("already a live room called standup", taken.Message);

        var left = await ise.Api.LeaveRoomAsync("standup", "ops"); // the last one out: it collapses
        Assert.Empty(left.Members);
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.RoomsAsync()).Single().Collapsed);
        var second = await wojtek.Api.CreateRoomAsync(new LocalRoomCreate("agent", "standup"));
        Assert.NotEqual(first.Id, second.Id);

        var stats = Assert.Single(router.Admin.CollapsedRooms(null));
        Assert.Equal(("standup", "empty"), (stats.Name, stats.Reason));
        Assert.Equal("ise/ops", Assert.Single(stats.Members).Member);
        Assert.Equal("standup", Assert.Single(router.Admin.Rooms()).Name);
    }

    [Fact]
    public async Task AnInviteeSeesNothingUntilItJoinsAndThenOnlyWhatFollows()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var invited = new List<LocalEvent>();
        await using var listening = new AgentConnection(wojtek.Api, "agent", e => { lock (invited) { invited.Add(e); } return Task.CompletedTask; });
        await listening.Registered.WaitAsync(TimeSpan.FromSeconds(10));

        var room = await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "plan", ["wojtek/agent"]));
        Assert.Equal("wojtek/agent", Assert.Single(room.Invited).Invitee);
        var before = await ise.Api.SendToRoomAsync("plan", new LocalRoomSend("ops", "before"));
        await RouterFixture.Eventually(() => { lock (invited) { return invited.Any(e => e.Kind == LocalEventKinds.RoomInvite && e.Invite?.Room == "plan"); } });
        Assert.Equal("ise/ops", Assert.Single(await wojtek.Api.RoomInvitesAsync("agent")).InvitedBy);
        await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.ReadRoomAsync("plan", "agent")); // not in it yet
        var sealedBefore = (await ise.Store.GetMessageAsync(ise.Keys.MachineId, before.Id))!.Envelope!;
        Assert.DoesNotContain(sealedBefore.Wraps, w => w.Machine == wojtek.Keys.MachineId); // and can't read it, ever

        var joined = await wojtek.Api.JoinRoomAsync("plan", "agent");
        Assert.Equal(["ise/ops", "wojtek/agent"], joined.Members.Order());
        Assert.Empty(joined.Invited);
        await ise.Api.SendToRoomAsync("plan", new LocalRoomSend("ops", "after"));

        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.ReadRoomAsync("plan", "agent")).Count == 1);
        Assert.Equal(["after"], (await wojtek.Api.ReadRoomAsync("plan", "agent")).Select(m => m.Body));
        Assert.Equal(["before", "after"], (await ise.Api.ReadRoomAsync("plan", "ops")).Select(m => m.Body));
        Assert.Empty(await wojtek.Api.RoomInvitesAsync("agent"));
    }

    [Fact]
    public async Task EveryMemberReadsARoomMessageAndNoOneElse()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        await using var dazzle = await DaemonFixture.StartAsync(router, "dazzle"); // never invited
        await OpenAsync(ise, "ops", "release", (wojtek, "agent"), (blinky, "bot"));

        var events = new List<LocalEvent>();
        await using (var listening = new AgentConnection(wojtek.Api, "agent", e => { lock (events) { events.Add(e); } return Task.CompletedTask; }))
        {
            await listening.Registered.WaitAsync(TimeSpan.FromSeconds(10));
            var sent = Assert.Single(await ise.Api.SendAsync(new LocalSendRequest("ops", ["room:release"], "ship it?")));
            Assert.Equal("room:release", sent.To);
            await RouterFixture.Eventually(() => { lock (events) { return events.Any(e => e.Kind == LocalEventKinds.RoomMessage); } });
        }

        await RouterFixture.EventuallyAsync(async () => (await blinky.Api.ReadRoomAsync("release", "bot")).Count == 1);
        var said = (await wojtek.Api.ReadRoomAsync("release", "agent")).Single();
        Assert.Equal(("ise/ops", "room:release"), (said.From, said.To));
        await wojtek.Api.SendToRoomAsync("release", new LocalRoomSend("agent", "yes", said.Id));
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.ReadRoomAsync("release", "ops")).Count == 2);
        Assert.Equal([null, said.Id], (await ise.Api.ReadRoomAsync("release", "ops")).Select(m => m.ReplyTo));

        Assert.Empty(await dazzle.Api.RoomsAsync());
        Assert.Equal(0, dazzle.CountMessages("r.to_machine LIKE 'room:%'"));
        Assert.Empty(await wojtek.Api.InboxAsync("agent")); // a room isn't an inbox
        foreach (var side in new[] { ise, wojtek, blinky }) // and has no receipts
        {
            Assert.Equal(0, side.CountMessages("r.type = 'receipt' AND r.reply_to IN (SELECT id FROM messages WHERE to_machine LIKE 'room:%')"));
        }

        var live = Assert.Single(router.Admin.Rooms());
        Assert.Equal((3, 2), (live.Members.Count, live.Messages));
    }

    [Fact]
    public async Task OnlyAMemberCanInviteAndAForgedEventIsRefused()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var room = await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "core", ["wojtek/agent"]));
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.RoomInvitesAsync("agent")).Count == 1);

        var refused = await Assert.ThrowsAsync<LocalApiException>(() => wojtek.Api.InviteToRoomAsync("core", new LocalRoomInviteRequest("agent", ["wojtek/sneaky"])));
        Assert.Contains("isn't in core", refused.Message);

        // Its machine signs the invite anyway (as a misbehaving machine would): the router replays the log and refuses it.
        var relay = wojtek.Daemon.Relay;
        var invite = relay.SignRoomEvent(new RoomEvent(Guid.NewGuid(), room.Id, RoomEventKind.Invite, $"{wojtek.Keys.MachineId}/agent",
            $"{wojtek.Keys.MachineId}/sneaky", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), "", ""));
        var byRouter = await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SubmitRoomEventAsync(invite));
        Assert.Contains("only a member can invite", byRouter.Message);

        // Nor can an event be altered after signing, or claimed for another machine.
        var join = relay.SignRoomEvent(new RoomEvent(Guid.NewGuid(), room.Id, RoomEventKind.Join, $"{wojtek.Keys.MachineId}/agent", null, null,
            ise.Store.GetRoom(room.Id)!.Events[1].EventId, DateTimeOffset.UtcNow, null, "", "")); // the invite's id
        var altered = join with { Actor = $"{wojtek.Keys.MachineId}/other" };
        Assert.Contains("signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SubmitRoomEventAsync(altered))).Message);
        var log = ise.Store.GetRoom(room.Id)!.Events;
        Assert.Throws<RoomLogException>(() => RoomLog.Replay([.. log, altered], _ => true, TimeSpan.FromMinutes(5))); // as each daemon checks it
        Assert.Throws<RoomLogException>(() => RoomLog.Replay([log[0] with { Name = "renamed" }], _ => true, TimeSpan.FromMinutes(5)));

        await relay.SubmitRoomEventAsync(join); // the real one
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.RoomsAsync("ops")).Single().Members.Count == 2);
    }

    [Fact]
    public async Task ASenderThatMissedALeaveIsRefusedAndSealsAgain()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        var room = await OpenAsync(ise, "ops", "ops", (wojtek, "agent"), (blinky, "bot"));
        await blinky.Api.LeaveRoomAsync("ops", "bot");

        // Sealed for the members as they were (blinky included): the router refuses it.
        var relay = ise.Daemon.Relay;
        var machines = new[] { await relay.ResolveAsync("wojtek"), await relay.ResolveAsync("blinky") };
        var stale = ise.Store.Transact(DateTimeOffset.UtcNow, tx => relay.ComposeRoom(tx, room.Id, machines, new OutgoingMessage("stale") { FromAgent = "ops" }));
        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => relay.RelayComposedAsync([stale]));
        Assert.Contains(RoomRefusals.MembersChanged, refusal.Message);

        var sent = await ise.Api.SendToRoomAsync("ops", new LocalRoomSend("ops", "just us now"));
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.ReadRoomAsync("ops", "agent")).Count == 1);
        Assert.DoesNotContain((await ise.Store.GetMessageAsync(ise.Keys.MachineId, sent.Id))!.Envelope!.Wraps, w => w.Machine == blinky.Keys.MachineId);
        await Task.Delay(300);
        Assert.Equal(0, blinky.CountMessages("r.to_machine LIKE 'room:%'"));
        Assert.Empty(await blinky.Api.ReadRoomAsync("ops", "bot")); // it was in, but nothing was said while it was
    }

    [Fact]
    public async Task AnIdleRoomCollapsesAndTheRouterKeepsOnlyItsStatistics()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await OpenAsync(ise, "ops", "quiet", (wojtek, "agent"));
        await ise.Api.SendToRoomAsync("quiet", new LocalRoomSend("ops", "zebra-crossing"));
        await wojtek.Api.SendToRoomAsync("quiet", new LocalRoomSend("agent", "quokka-sighting"));

        router.SetSetting("PoniExp:RoomIdleExpiry", "00:00:01");
        await Task.Delay(1200);
        await router.Rooms.SweepAsync();

        Assert.Empty(router.Admin.Rooms());
        var stats = Assert.Single(router.Admin.CollapsedRooms(null));
        Assert.Equal(("quiet", "idle", 2), (stats.Name, stats.Reason, stats.Messages.Count));
        Assert.Equal([("ise", 1), ("wojtek", 1)], stats.Machines.Select(m => (m.Machine, m.Messages)).Order());
        Assert.All(stats.Members, m => Assert.NotNull(m.LeftOn));
        var routerData = string.Concat(Directory.GetFiles(router.DataDirectory, "router.db*").Select(EncryptionTests.ReadShared));
        Assert.DoesNotContain("zebra-crossing", routerData); // no bodies, ever
        Assert.DoesNotContain("quokka-sighting", routerData);

        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.RoomsAsync("agent")).Single().Collapsed);
        Assert.Equal(["zebra-crossing", "quokka-sighting"], (await wojtek.Api.ReadRoomAsync("quiet", "agent")).Select(m => m.Body)); // each machine keeps its copy
        var closed = await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.SendToRoomAsync("quiet", new LocalRoomSend("ops", "anyone?")));
        Assert.Contains("collapsed", closed.Message);
    }

    [Fact]
    public async Task ARemovedMachinesAgentsAreOutOfItsRooms()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var room = await OpenAsync(ise, "ops", "ops", (wojtek, "agent"));

        await router.Admin.RemoveAsync("wojtek");

        await RouterFixture.EventuallyAsync(async () => (await ise.Api.RoomsAsync("ops")).Single().Members.Count == 1);
        await ise.Api.SendToRoomAsync("ops", new LocalRoomSend("ops", "carry on")); // sealed for ise alone, accepted
        Assert.Equal(1, Assert.Single(router.Admin.Rooms()).Messages);
        Assert.Equal(room.Id, Assert.Single(router.Admin.Rooms()).RoomId);
    }

    [Fact]
    public async Task ARoomOnOneMachineWorksAndIsCounted()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "local", ["ise/notes"]));
        await ise.Api.JoinRoomAsync("local", "notes");
        await ise.Api.SendToRoomAsync("local", new LocalRoomSend("ops", "note to self"));

        Assert.Equal(["note to self"], (await ise.Api.ReadRoomAsync("local", "notes")).Select(m => m.Body));
        Assert.Equal(0, router.Queue.Count);
        Assert.Equal(1, Assert.Single(router.Admin.Rooms()).Messages);
    }

    // ---- helpers ----

    /// <summary>Opens a room and has each invitee join it.</summary>
    private static async Task<LocalRoom> OpenAsync(DaemonFixture creator, string agent, string name, params (DaemonFixture Daemon, string Agent)[] members)
    {
        var room = await creator.Api.CreateRoomAsync(new LocalRoomCreate(agent, name, [.. members.Select(m => $"{m.Daemon.Name}/{m.Agent}")]));
        foreach (var (daemon, member) in members)
        {
            await RouterFixture.EventuallyAsync(async () => (await daemon.Api.RoomInvitesAsync(member)).Count == 1);
            await daemon.Api.JoinRoomAsync(name, member);
        }

        await RouterFixture.EventuallyAsync(async () => (await creator.Api.RoomsAsync(agent)).Single().Members.Count == members.Length + 1);
        return room;
    }

    private static async Task RegisterAndLeaveAsync(DaemonFixture daemon, string agent, IReadOnlyList<string>? roles = null)
    {
        await using var connection = new AgentConnection(daemon.Api, agent, _ => Task.CompletedTask, roles: roles);
        await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
