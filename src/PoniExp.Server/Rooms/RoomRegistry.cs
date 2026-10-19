using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;
using PoniExp.Server.Hubs;
using PoniExp.Server.Machines;

namespace PoniExp.Server.Rooms;

/// <summary>
/// Rooms on the router (see docs/plan.md, Rooms): unique names while live, each room's signed membership log
/// (checked here and by every member), the membership check on room messages, collapse, and the statistics a
/// collapsed room leaves. The router orders the log and can withhold an event, but can't forge or change one.
/// </summary>
public sealed class RoomRegistry
{
    /// <summary>The most message entries a room's statistics keep (beyond that, only the per-machine counts grow).</summary>
    public const int MaxMessageStats = 100_000;

    private readonly RouterStore _store;
    private readonly MachineRegistry _machines;
    private readonly IHubContext<RelayHub> _hub;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RoomRegistry> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, LiveRoom> _rooms = [];

    public RoomRegistry(RouterStore store, MachineRegistry machines, IHubContext<RelayHub> hub, IOptionsMonitor<RouterOptions> options, TimeProvider time,
        ILogger<RoomRegistry> logger)
    {
        (_store, _machines, _hub, _options, _time, _logger) = (store, machines, hub, options, time, logger);
        foreach (var record in store.LoadLiveRooms())
        {
            try
            {
                _rooms[record.RoomId] = new LiveRoom(record, Replay(record.Events));
            }
            catch (RoomLogException e)
            {
                logger.LogWarning("Room {Name} ({Id}) has a log that no longer checks out and is set aside: {Why}", record.Name, record.RoomId, e.Message);
            }
        }
    }

    /// <summary>Opens a room: the name must be free among live rooms; the caller's machine signed the create.</summary>
    public async Task<RoomState> CreateAsync(string caller, RoomEvent create)
    {
        RoomState state;
        lock (_lock)
        {
            RequireAuthor(caller, create);
            if (create.Kind != RoomEventKind.Create)
            {
                throw new RouterException("Open a room with a create event.");
            }

            var view = Replay([create]);
            if (_rooms.Values.Any(r => r.Record.Name == view.Name))
            {
                throw new RouterException($"There's already a live room called {view.Name}.", RouterErrorKind.Conflict);
            }

            if (_rooms.ContainsKey(view.RoomId) || _store.RoomExists(view.RoomId))
            {
                throw new RouterException("That room id is taken.", RouterErrorKind.Conflict);
            }

            var room = new LiveRoom(new LiveRoomRecord(view.RoomId, view.Name, [create], create.OccurredOn, create.OccurredOn, [], []), view);
            _rooms[view.RoomId] = room;
            _store.SaveLiveRoom(room.Record);
            state = room.State;
        }

        _logger.LogInformation("Room {Name} ({Id}) opened", state.Name, state.RoomId.ToString()[..8]);
        await PushAsync(state, []);
        return state;
    }

    /// <summary>Appends an invite, join or leave by the caller's machine. A room left empty collapses.</summary>
    public async Task<RoomState> SubmitAsync(string caller, RoomEvent e)
    {
        RoomState state;
        IReadOnlyCollection<string> before;
        lock (_lock)
        {
            RequireAuthor(caller, e);
            if (e.Kind is not (RoomEventKind.Invite or RoomEventKind.Join or RoomEventKind.Leave))
            {
                throw new RouterException("Only invite, join and leave can be added to a room.");
            }

            var room = Live(e.RoomId);
            before = Involved(room.View);
            List<RoomEvent> events = [.. room.Record.Events, e];
            var view = Replay(events);
            room.Record = room.Record with { Events = events, LastActivityOn = Max(room.Record.LastActivityOn, e.OccurredOn) };
            room.View = view;
            _store.SaveLiveRoom(room.Record);
            state = room.State;
        }

        await PushAsync(state, before);
        if (state.Events.Count > 0 && Replay(state.Events).Members.Count == 0)
        {
            await CollapseAsync(state.RoomId, "empty");
        }

        return state;
    }

    /// <summary>A room the caller's machine is (or was) involved in: its log, or that it has collapsed.</summary>
    public RoomState Get(string caller, Guid roomId)
    {
        lock (_lock)
        {
            if (_rooms.TryGetValue(roomId, out var room))
            {
                return room.View.Involves(caller) ? room.State : throw new RouterException("No such room.", RouterErrorKind.NotFound);
            }
        }

        return _store.LoadRoomStats(roomId) is { } stats && stats.Members.Any(m => Address.Parse(m.Member).Machine == caller)
            ? new RoomState(roomId, stats.Name, [], true, stats.Reason)
            : throw new RouterException("No such room.", RouterErrorKind.NotFound);
    }

    /// <summary>The live rooms the caller is involved in, and what became of the <paramref name="known"/> ones that aren't live.</summary>
    public IReadOnlyList<RoomState> Mine(string caller, IReadOnlyCollection<Guid> known)
    {
        List<RoomState> states;
        lock (_lock)
        {
            states = [.. _rooms.Values.Where(r => r.View.Involves(caller)).Select(r => r.State)];
        }

        foreach (var id in known.Where(id => states.All(s => s.RoomId != id)).Take(1000))
        {
            try
            {
                states.Add(Get(caller, id));
            }
            catch (RouterException)
            {
                // never involved, or long gone: nothing to say
            }
        }

        return states;
    }

    /// <summary>
    /// Checks a room message: the room is live, the sender's machine has a member in it, and the content key is
    /// wrapped for exactly the members' machines. Returns the machines to deliver to (all but the sender's).
    /// </summary>
    public IReadOnlyList<string> CheckMessage(string caller, Envelope envelope, Guid roomId)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out var room))
            {
                throw new RouterException(_store.RoomExists(roomId) ? $"{RoomRefusals.Collapsed}." : "No such room.", RouterErrorKind.NotFound);
            }

            var machines = room.View.MemberMachines;
            if (!machines.Contains(caller))
            {
                throw new RouterException("None of your machine's agents is in that room.");
            }

            var wrapped = (envelope.Wraps ?? []).Select(w => w.Machine).ToList();
            if (wrapped.Count != wrapped.Distinct().Count() || !wrapped.ToHashSet().SetEquals(machines))
            {
                throw new RouterException($"{RoomRefusals.MembersChanged}: seal it for the members' machines as they are now.");
            }

            return [.. machines.Where(m => m != caller)];
        }
    }

    /// <summary>Counts a relayed room message (for the room's statistics, and as activity).</summary>
    public void Record(Guid roomId, Envelope envelope)
    {
        lock (_lock)
        {
            if (_rooms.TryGetValue(roomId, out var room))
            {
                var now = _time.GetUtcNow();
                var messages = room.Record.Messages.Count < MaxMessageStats ? [.. room.Record.Messages, new RoomMessageStat(now, envelope.FromMachine, envelope.Size)]
                    : room.Record.Messages;
                var counts = room.Record.Machines.ToDictionary(m => m.Machine);
                counts[envelope.FromMachine] = counts.TryGetValue(envelope.FromMachine, out var c) ? c with { Messages = c.Messages + 1, Bytes = c.Bytes + envelope.Size }
                    : new RoomMachineStats(envelope.FromMachine, 1, envelope.Size);
                room.Record = room.Record with { Messages = messages, Machines = [.. counts.Values], LastActivityOn = now };
                _store.SaveLiveRoom(room.Record);
            }
        }
    }

    /// <summary>A removed machine's agents are out of every room (a router event, accepted because the machine isn't approved).</summary>
    public async Task MachineRemovedAsync(string machineId)
    {
        List<Guid> affected;
        lock (_lock)
        {
            affected = [.. _rooms.Values.Where(r => r.View.Involves(machineId)).Select(r => r.View.RoomId)];
        }

        foreach (var id in affected)
        {
            RoomState state;
            IReadOnlyCollection<string> before;
            lock (_lock)
            {
                if (!_rooms.TryGetValue(id, out var room))
                {
                    continue;
                }

                before = Involved(room.View);
                var removed = new RoomEvent(Guid.NewGuid(), id, RoomEventKind.Removed, "router", machineId, null, null, _time.GetUtcNow(), null, "", "");
                List<RoomEvent> events = [.. room.Record.Events, removed];
                room.View = Replay(events);
                room.Record = room.Record with { Events = events };
                _store.SaveLiveRoom(room.Record);
                state = room.State;
            }

            await PushAsync(state, before);
            if (Replay(state.Events).Members.Count == 0)
            {
                await CollapseAsync(id, "empty");
            }
        }
    }

    /// <summary>Collapses rooms nobody has said or done anything in for <see cref="RouterOptions.RoomIdleExpiry"/>; drops old statistics.</summary>
    public async Task SweepAsync()
    {
        var now = _time.GetUtcNow();
        List<Guid> idle;
        lock (_lock)
        {
            idle = [.. _rooms.Values.Where(r => now - r.Record.LastActivityOn >= _options.CurrentValue.RoomIdleExpiry).Select(r => r.Record.RoomId)];
        }

        foreach (var id in idle)
        {
            await CollapseAsync(id, "idle");
        }

        _store.DeleteRoomStatsBefore(now - _options.CurrentValue.RoomStatsRetention);
    }

    public IReadOnlyList<RoomSummary> Summaries()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            return [.. _rooms.Values.OrderBy(r => r.Record.Name).Select(r => new RoomSummary(r.Record.RoomId, r.Record.Name, r.Record.OpenedOn,
                r.Record.LastActivityOn, [.. r.View.Members.Keys.Select(Display)], [.. RoomLog.Open(r.View, now).Select(i => Display(i.Invitee))],
                r.Record.Machines.Sum(m => m.Messages)))];
        }
    }

    public IReadOnlyList<RoomStats> Collapsed(DateTimeOffset? since) => _store.LoadRoomStats(since);

    /// <summary>Shows <c>machineId/agent</c> as <c>machine-name/agent</c> (owner views only).</summary>
    public string Display(string member)
    {
        var address = Address.Parse(member);
        return $"{_machines.Get(address.Machine)?.Name ?? address.Machine[..Math.Min(8, address.Machine.Length)]}/{address.Agent}";
    }

    // ---- inside ----

    private async Task CollapseAsync(Guid roomId, string reason)
    {
        RoomState state;
        IReadOnlyCollection<string> involved;
        lock (_lock)
        {
            if (!_rooms.Remove(roomId, out var room))
            {
                return;
            }

            var now = _time.GetUtcNow();
            involved = Involved(room.View);
            var history = room.View.History.Select(h => h.LeftOn is null ? h with { LeftOn = now } : h).ToList();
            _store.CollapseRoom(new RoomStats(roomId, room.Record.Name, room.Record.OpenedOn, now, reason, history, room.Record.Machines, room.Record.Messages));
            state = new RoomState(roomId, room.Record.Name, [], true, reason);
        }

        _logger.LogInformation("Room {Name} ({Id}) collapsed: {Reason}", state.Name, roomId.ToString()[..8], reason);
        await PushAsync(state, involved);
    }

    private RoomView Replay(IReadOnlyList<RoomEvent> events)
    {
        try
        {
            return RoomLog.Replay(events, id => _machines.Get(id) is { Status: MembershipStatus.Approved }, _options.CurrentValue.ClockSkew);
        }
        catch (RoomLogException e)
        {
            throw new RouterException($"Refused: {e.Message}.");
        }
    }

    private void RequireAuthor(string caller, RoomEvent e)
    {
        Address actor;
        try
        {
            actor = Address.Parse(e.Actor);
        }
        catch (FormatException)
        {
            throw new RouterException("A room event's actor is machine/agent.");
        }

        if (actor.Machine != caller || !RoomLog.IsSignedBy(e, caller))
        {
            throw new RouterException("A room event must be your own machine's, signed with its key.");
        }

        if ((e.OccurredOn - _time.GetUtcNow()).Duration() > _options.CurrentValue.ClockSkew)
        {
            throw new RouterException("The room event's time is too far from the router's; check this machine's clock.");
        }
    }

    private LiveRoom Live(Guid roomId) => _rooms.TryGetValue(roomId, out var room) ? room
        : throw new RouterException(_store.RoomExists(roomId) ? $"{RoomRefusals.Collapsed}." : "No such room.", RouterErrorKind.NotFound);

    /// <summary>Machines with a member, an invitee or a past member.</summary>
    private static HashSet<string> Involved(RoomView view) =>
        [.. view.History.Select(h => Address.Parse(h.Member).Machine), .. view.Invitations.Select(i => Address.Parse(i.Invitee).Machine)];

    /// <summary>Tells every machine involved now or before the change.</summary>
    private async Task PushAsync(RoomState state, IReadOnlyCollection<string> before)
    {
        HashSet<string> machines = [.. before];
        if (state.Events.Count > 0)
        {
            machines.UnionWith(Involved(RoomLog.Replay(state.Events, _ => false, RoomLog.AlreadyChecked)));
        }

        foreach (var machine in machines)
        {
            await _hub.Clients.Group(RelayHub.MachineGroup(machine)).SendAsync(HubMethods.RoomChanged, state);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private sealed class LiveRoom(LiveRoomRecord record, RoomView view)
    {
        public LiveRoomRecord Record { get; set; } = record;
        public RoomView View { get; set; } = view;
        public RoomState State => new(Record.RoomId, Record.Name, Record.Events);
    }
}

/// <summary>A live room in router.db: its log, and the counts its statistics will keep.</summary>
public sealed record LiveRoomRecord(Guid RoomId, string Name, IReadOnlyList<RoomEvent> Events, DateTimeOffset OpenedOn, DateTimeOffset LastActivityOn,
    IReadOnlyList<RoomMachineStats> Machines, IReadOnlyList<RoomMessageStat> Messages);
