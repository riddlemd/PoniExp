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
    public async Task EveryMemberReadsARoomMessageAndNoOneElse()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        await using var dazzle = await DaemonFixture.StartAsync(router, "dazzle"); // not a member

        var room = await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "release", ["wojtek/agent", "blinky/bot"]));
        await KnowsRoomAsync(wojtek, room.Id, 1);
        await KnowsRoomAsync(blinky, room.Id, 1);
        var events = new List<LocalEvent>();
        await using (var listening = new AgentConnection(wojtek.Api, "agent", e => { lock (events) { events.Add(e); } return Task.CompletedTask; }))
        {
            await listening.Registered.WaitAsync(TimeSpan.FromSeconds(10));
            var sent = await ise.Api.SendToRoomAsync("release", new LocalRoomSend("ops", "ship it?"));
            Assert.Equal("room:release", sent.To);
            await RouterFixture.Eventually(() => { lock (events) { return events.Any(e => e.Kind == LocalEventKinds.RoomMessage); } });
        }

        Assert.Equal(["ship it?"], (await blinky.Api.ReadRoomAsync("release", "bot")).Select(m => m.Body));
        var said = (await wojtek.Api.ReadRoomAsync(room.Id.ToString(), "agent")).Single();
        Assert.Equal(("ise/ops", "room:release", room.Id), (said.From, said.To, said.ConversationId));
        await wojtek.Api.SendToRoomAsync("release", new LocalRoomSend("agent", "yes", said.Id));
        await RouterFixture.EventuallyAsync(async () => (await ise.Api.ReadRoomAsync("release", "ops")).Count == 2);
        Assert.Equal([null, said.Id], (await ise.Api.ReadRoomAsync("release", "ops")).Select(m => m.ReplyTo));

        Assert.Empty(await dazzle.Api.RoomsAsync());
        Assert.Equal(0, dazzle.CountMessages("r.to_machine LIKE 'room:%'"));
        Assert.Empty(await wojtek.Api.InboxAsync("agent")); // a room isn't an inbox
        foreach (var side in new[] { ise, wojtek, blinky }) // no receipts in rooms
        {
            Assert.Equal(0, side.CountMessages("r.type = 'receipt' AND r.reply_to IN (SELECT id FROM messages WHERE to_machine LIKE 'room:%')"));
        }
    }

    [Fact]
    public async Task ARemovedMemberIsLeftOutOfTheNextMessage()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        var room = await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "ops", ["wojtek/agent", "blinky/bot"]));
        await KnowsRoomAsync(blinky, room.Id, 1);

        var changed = await ise.Api.ChangeRoomAsync("ops", new LocalRoomChange("ops", new RoomChange(Remove: ["blinky/bot"])));
        Assert.Equal(2, changed.Version);
        await KnowsRoomAsync(blinky, room.Id, 2);
        await KnowsRoomAsync(wojtek, room.Id, 2);
        var sent = await ise.Api.SendToRoomAsync("ops", new LocalRoomSend("ops", "just us now"));

        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.ReadRoomAsync("ops", "agent")).Count == 1);
        var envelope = (await ise.Store.GetMessageAsync(ise.Keys.MachineId, sent.Id))!.Envelope!;
        Assert.DoesNotContain(envelope.Wraps, w => w.Machine == blinky.Keys.MachineId); // not wrapped for: it can't read it
        await Task.Delay(300);
        Assert.Equal(0, blinky.CountMessages("r.id = $id", ("$id", sent.Id.ToString("D"))));
        await Assert.ThrowsAsync<LocalApiException>(() => blinky.Api.ReadRoomAsync("ops", "bot"));
    }

    [Fact]
    public async Task AChangeFromANonAdminIsIgnoredButAMemberMayLeave()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        var room = await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "core", ["wojtek/agent"]));
        await KnowsRoomAsync(wojtek, room.Id, 1);

        var refused = await Assert.ThrowsAsync<LocalApiException>(() =>
            wojtek.Api.ChangeRoomAsync("core", new LocalRoomChange("agent", new RoomChange(Add: ["wojtek/sneaky"]))));
        Assert.Contains("only an admin", refused.Message);

        // Its daemon sends the change anyway (as a misbehaving machine would): ise's daemon ignores it.
        var current = wojtek.Store.GetRoom(room.Id)!.Snapshot;
        var forged = current with { Version = 2, Members = [.. current.Members, $"{wojtek.Keys.MachineId}/sneaky"], Admins = [.. current.Admins, $"{wojtek.Keys.MachineId}/agent"] };
        var iseInfo = await wojtek.Daemon.Relay.ResolveAsync("ise");
        wojtek.Store.Transact(DateTimeOffset.UtcNow, tx => wojtek.Daemon.Relay.Compose(tx, iseInfo, Address.DaemonAgent,
            new OutgoingMessage(JsonSerializer.Serialize(forged, WireProtocol.Json)) { FromAgent = "agent", Type = MessageType.Room, ContentType = "application/json" }));
        await wojtek.Daemon.Relay.FlushOutboxAsync();
        await RouterFixture.Eventually(() => ise.Store.GetDiagnostics().Any(d => d.Detail.Contains("isn't an admin", StringComparison.Ordinal)));
        Assert.Equal(1, ise.Store.GetRoom(room.Id)!.Snapshot.Version);

        var left = await wojtek.Api.ChangeRoomAsync("core", new LocalRoomChange("agent", new RoomChange(Leave: true)));
        Assert.DoesNotContain(left.Members, m => m.EndsWith("/agent", StringComparison.Ordinal));
        await KnowsRoomAsync(ise, room.Id, 2); // a member leaving is accepted from that member
        Assert.Equal([$"{ise.Keys.MachineId}/ops"], ise.Store.GetRoom(room.Id)!.Snapshot.Members);
    }

    [Fact]
    public async Task ANewMemberSeesOnlyWhatIsSaidAfterTheyJoin()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await using var wojtek = await DaemonFixture.StartAsync(router, "wojtek");
        await using var blinky = await DaemonFixture.StartAsync(router, "blinky");
        var room = await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "late", ["wojtek/agent"]));
        await KnowsRoomAsync(wojtek, room.Id, 1);
        await ise.Api.SendToRoomAsync("late", new LocalRoomSend("ops", "before"));
        await RouterFixture.EventuallyAsync(async () => (await wojtek.Api.ReadRoomAsync("late", "agent")).Count == 1);

        await ise.Api.ChangeRoomAsync("late", new LocalRoomChange("ops", new RoomChange(Add: ["blinky/bot"], AddAdmins: ["wojtek/agent"])));
        await KnowsRoomAsync(blinky, room.Id, 2);
        await KnowsRoomAsync(wojtek, room.Id, 2);
        await wojtek.Api.SendToRoomAsync("late", new LocalRoomSend("agent", "after")); // an admin now, and it knows blinky joined

        await RouterFixture.EventuallyAsync(async () => (await blinky.Api.ReadRoomAsync("late", "bot")).Count == 1);
        Assert.Equal(["after"], (await blinky.Api.ReadRoomAsync("late", "bot")).Select(m => m.Body));
        Assert.Equal(["before", "after"], (await ise.Api.ReadRoomAsync("late", "ops")).Select(m => m.Body));
        Assert.Contains("wojtek/agent", (await blinky.Api.RoomsAsync("bot")).Single().Admins);
    }

    [Fact]
    public async Task ARoomOnOneMachineNeedsNoRouterAndAClosedRoomTakesNoMore()
    {
        await using var router = await RouterFixture.StartAsync();
        await using var ise = await DaemonFixture.StartAsync(router, "ise");
        await ise.Api.CreateRoomAsync(new LocalRoomCreate("ops", "local", ["ise/notes"]));
        var sent = await ise.Api.SendToRoomAsync("local", new LocalRoomSend("ops", "note to self"));
        Assert.Equal(["note to self"], (await ise.Api.ReadRoomAsync("local", "notes")).Select(m => m.Body));
        Assert.Equal(0, router.Queue.Count);

        await ise.Api.ChangeRoomAsync("local", new LocalRoomChange("ops", new RoomChange(Close: true)));
        var closed = await Assert.ThrowsAsync<LocalApiException>(() => ise.Api.SendToRoomAsync("local", new LocalRoomSend("ops", "more")));
        Assert.Contains("closed", closed.Message);
        Assert.NotEqual(Guid.Empty, sent.Id);
    }

    // ---- helpers ----

    private static Task KnowsRoomAsync(DaemonFixture daemon, Guid room, long version) =>
        RouterFixture.Eventually(() => daemon.Store.GetRoom(room)?.Snapshot.Version == version);

    private static async Task RegisterAndLeaveAsync(DaemonFixture daemon, string agent, IReadOnlyList<string>? roles = null)
    {
        await using var connection = new AgentConnection(daemon.Api, agent, _ => Task.CompletedTask, roles: roles);
        await connection.Registered.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
