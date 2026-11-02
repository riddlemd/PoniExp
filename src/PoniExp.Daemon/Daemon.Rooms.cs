using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// Rooms (see docs/plan.md, Rooms). The router keeps each room's signed membership log; this daemon checks every log
/// it is given from <c>create</c> on, keeps it only if it grew, and derives the members from it. A room message is
/// sealed for exactly the members' machines; one arriving here is accepted only from an agent that was a member when
/// it was written, and shown to local agents that were members then. No receipts, no inbox.
/// </summary>
public sealed partial class Daemon
{
    /// <summary>Opens a room named <paramref name="name"/> with <paramref name="agent"/> in it, then invites <paramref name="invite"/>.</summary>
    internal async Task<LocalRoom> CreateRoomAsync(string agent, string name, IReadOnlyCollection<string> invite, CancellationToken cancellationToken)
    {
        if (!Limits.IsValidAgent(name))
        {
            throw new ArgumentException($"a room name is {Limits.AgentRule}");
        }

        var invitees = await InviteeIdsAsync(invite, agent, cancellationToken);
        var draft = _relay.SignRoomEvent(new RoomEvent(Guid.NewGuid(), Guid.Empty, RoomEventKind.Create, Me(agent), null, name, null, Now, null, "", ""));
        var create = _relay.SignRoomEvent(draft with { RoomId = RoomLog.RoomIdOf(draft) });
        var state = await _relay.CreateRoomAsync(create, cancellationToken);
        ApplyRoom(state);
        return invitees.Count == 0 ? ToLocalRoom(RequireRoom(state.RoomId.ToString())) : await InviteAsync(agent, state.RoomId.ToString(), invitees, cancellationToken);
    }

    /// <summary>A member invites agents. Each must accept (<see cref="JoinRoomAsync"/>) before the invitation expires.</summary>
    internal async Task<LocalRoom> InviteToRoomAsync(string agent, string room, IReadOnlyCollection<string> invite, CancellationToken cancellationToken) =>
        await InviteAsync(agent, room, await InviteeIdsAsync(invite, agent, cancellationToken), cancellationToken);

    internal async Task<LocalRoom> JoinRoomAsync(string agent, string room, CancellationToken cancellationToken)
    {
        var record = await FreshRoomAsync(room, cancellationToken);
        var invitation = RoomLog.Open(View(record), Now).FirstOrDefault(i => i.Invitee == Me(agent))
                         ?? throw new InvalidOperationException($"{agent} has no open invitation to {record.Name}");
        await SubmitAsync(new RoomEvent(Guid.NewGuid(), record.Id, RoomEventKind.Join, Me(agent), null, null, invitation.InviteId, Now, null, "", ""),
            cancellationToken);
        return ToLocalRoom(RequireRoom(record.Id.ToString()));
    }

    internal async Task<LocalRoom> LeaveRoomAsync(string agent, string room, CancellationToken cancellationToken)
    {
        var record = await FreshRoomAsync(room, cancellationToken);
        RequireMember(record, agent);
        await SubmitAsync(new RoomEvent(Guid.NewGuid(), record.Id, RoomEventKind.Leave, Me(agent), null, null, null, Now, null, "", ""), cancellationToken);
        return ToLocalRoom(RequireRoom(record.Id.ToString()));
    }

    /// <summary>
    /// Says something in the room: sealed for exactly the members' machines as the router has them. If someone joined
    /// or left in between, the router refuses it, and it is sealed again for the members as they are now.
    /// </summary>
    internal async Task<SentMessage> SendToRoomAsync(string agent, string room, OutgoingMessage message, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var record = await FreshRoomAsync(room, cancellationToken);
            RequireMember(record, agent);
            var machines = new List<MachineInfo>();
            foreach (var machine in View(record).MemberMachines.Where(m => m != MachineId))
            {
                machines.Add(await _relay.ResolveAsync(machine, cancellationToken));
            }

            var envelope = Store.Transact(Now, tx => _relay.ComposeRoom(tx, record.Id, machines, message with { FromAgent = agent }));
            try
            {
                var sent = (await _relay.RelayComposedAsync([envelope], cancellationToken))[0];
                PushToRoom(record, Store.GetRoomMessages(MachineId, record.Id, null).Single(m => m.Id == envelope.Id), MessageDirection.Out, exceptAgent: agent);
                return sent;
            }
            catch (Microsoft.AspNetCore.SignalR.HubException e) when (e.Message.Contains(RoomRefusals.MembersChanged, StringComparison.Ordinal) && attempt < 3)
            {
                // someone joined or left since: seal it again
            }
        }
    }

    /// <summary>The room's messages as <paramref name="agent"/> saw them: only those written while it was a member.</summary>
    internal IReadOnlyList<LocalMessage> ReadRoom(string agent, string room, DateTimeOffset? since, int limit)
    {
        var record = RequireRoom(room);
        var spans = View(record).History.Where(h => h.Member == Me(agent)).ToList();
        if (spans.Count == 0)
        {
            throw new InvalidOperationException($"{agent} has never been in {record.Name}");
        }

        return [.. Store.GetRoomMessages(MachineId, record.Id, since, limit)
            .Where(m => spans.Any(s => Within(s, m.CreatedOn)))
            .Select(m => ToLocal(m, m.FromMachine == MachineId ? MessageDirection.Out : MessageDirection.In))];
    }

    /// <summary>Rooms an agent here is in, was in, or is invited to (or just <paramref name="agent"/>'s).</summary>
    internal IReadOnlyList<LocalRoom> Rooms(string? agent)
    {
        var rooms = new List<LocalRoom>();
        foreach (var record in Store.GetRooms())
        {
            var view = TryView(record);
            if (view is null)
            {
                continue;
            }

            bool Mine(string member) => agent is null ? Address.Parse(member).Machine == MachineId : member == Me(agent);
            if (view.History.Any(h => Mine(h.Member)) || RoomLog.Open(view, Now).Any(i => Mine(i.Invitee)))
            {
                rooms.Add(ToLocalRoom(record, view));
            }
        }

        return rooms;
    }

    internal IReadOnlyList<LocalRoomInvite> RoomInvites(string? agent) =>
        [.. Rooms(agent).Where(r => !r.Collapsed).SelectMany(r => r.Invited)
            .Where(i => agent is null ? i.Invitee.StartsWith($"{OwnName}/", StringComparison.Ordinal) : i.Invitee == $"{OwnName}/{agent}")];

    internal LocalRoom ToLocalRoom(RoomRecord record) => ToLocalRoom(record, View(record));

    private LocalRoom ToLocalRoom(RoomRecord record, RoomView view) =>
        new(record.Id, record.Name, Display(view.Creator), view.OpenedOn, [.. view.Members.Keys.Select(Display)],
            record.Collapsed ? [] : [.. RoomLog.Open(view, Now).Select(i => new LocalRoomInvite(record.Id, record.Name, Display(i.Invitee), Display(i.Inviter), i.ExpiresOn))],
            record.Collapsed, record.CollapseReason);

    /// <summary>A room by id, id prefix or name; a live one wins over collapsed ones of the same name.</summary>
    internal RoomRecord RequireRoom(string room)
    {
        if (room.StartsWith(RoomAddress.Prefix, StringComparison.Ordinal))
        {
            room = room[RoomAddress.Prefix.Length..];
        }

        if (Guid.TryParse(room, out var id) && Store.GetRoom(id) is { } byId)
        {
            return byId;
        }

        var matches = Store.GetRooms().Where(r => r.Name == room || r.Id.ToString().StartsWith(room, StringComparison.Ordinal)).ToList();
        var live = matches.Where(r => !r.Collapsed).ToList();
        return (live.Count > 0 ? live : matches) switch
        {
            [var one] => one,
            [] => throw new KeyNotFoundException($"no room '{room}' on this machine"),
            var several => live.Count > 0 ? throw new ArgumentException($"'{room}' matches {several.Count} rooms; use its id") : several[0],
        };
    }

    // ---- from the router ----

    /// <summary>
    /// A room's log from the router: checked from <c>create</c> on and kept only if it extends what this machine had (the
    /// router can withhold events, not rewrite them). Local agents hear of invitations, joins, leaves and collapse.
    /// </summary>
    // The router's push and an API call's reply apply on different threads; unserialised, a stale copy that read
    // "nothing known" can save over a longer log it raced.
    private readonly Lock _roomApply = new();

    internal void ApplyRoom(RoomState state) => ApplyRoom(state, retried: false);

    private void ApplyRoom(RoomState state, bool retried)
    {
        lock (_roomApply)
        {
            ApplyRoomLocked(state, retried);
        }
    }

    private void ApplyRoomLocked(RoomState state, bool retried)
    {
        try
        {
            var known = Store.GetRoom(state.RoomId);
            if (state.Collapsed)
            {
                if (known is { Collapsed: false })
                {
                    Store.Transact(Now, tx => tx.CollapseRoom(state.RoomId, state.CollapseReason ?? "collapsed"));
                    Tell(Store.GetRoom(state.RoomId)!, View(known), null);
                }

                return;
            }

            var view = RoomLog.Replay(state.Events, IsApprovedMachine, _messaging.CurrentValue.ClockSkew);
            if (known is not null && (known.Events.Count > state.Events.Count
                                      || !known.Events.Select(e => e.EventId).SequenceEqual(state.Events.Take(known.Events.Count).Select(e => e.EventId))))
            {
                throw new RoomLogException("it doesn't extend the log this machine has");
            }

            if (known is not null && known.Events.Count == state.Events.Count)
            {
                return;
            }

            Store.Transact(Now, tx => tx.SaveRoom(state.RoomId, view.Name, state.Events));
            Tell(Store.GetRoom(state.RoomId)!, known is null ? null : View(known), view);
        }
        catch (RoomLogException) when (!retried && state.Events.Any(e => e.Kind == RoomEventKind.Removed))
        {
            // The owner removed a machine this one still lists: refresh the list, then look again.
            Background(async () =>
            {
                await _relay.RefreshMachinesAsync();
                ApplyRoom(state, retried: true);
            });
        }
        catch (RoomLogException e)
        {
            Diagnose("warning", "room", $"ignored the router's copy of room {state.Name} ({state.RoomId.ToString()[..8]}): {e.Message}");
        }
    }

    /// <summary>After a (re)connect: rooms this machine knows that the router has since collapsed (live ones arrive on connect).</summary>
    private async Task SyncRoomsAsync()
    {
        var known = Store.GetRooms().Where(r => !r.Collapsed).Select(r => r.Id).ToList();
        foreach (var state in await _relay.GetMyRoomsAsync(known))
        {
            ApplyRoom(state);
        }
    }

    private void Tell(RoomRecord record, RoomView? before, RoomView? after)
    {
        var room = ToLocalRoom(record, after ?? before!);
        var invitedBefore = before is null ? [] : RoomLog.Open(before, Now).Select(i => i.InviteId).ToHashSet();
        foreach (var invitation in after is null ? [] : RoomLog.Open(after, Now).Where(i => !invitedBefore.Contains(i.InviteId)))
        {
            if (Address.Parse(invitation.Invitee) is { } invitee && invitee.Machine == MachineId && _sessions.Get(invitee.Agent!) is { } session)
            {
                session.Push(new LocalEvent(LocalEventKinds.RoomInvite, Room: room,
                    Invite: new LocalRoomInvite(record.Id, record.Name, Display(invitation.Invitee), Display(invitation.Inviter), invitation.ExpiresOn)));
            }
        }

        if (before is not null && after is not null && before.Members.Keys.ToHashSet().SetEquals(after.Members.Keys))
        {
            return; // an invitation only: members hear of the invitee when it joins
        }

        var members = (before?.Members.Keys ?? []).Concat(after?.Members.Keys ?? []).Distinct().Select(Address.Parse).Where(a => a.Machine == MachineId);
        foreach (var member in members)
        {
            _sessions.Get(member.Agent!)?.Push(new LocalEvent(LocalEventKinds.RoomChanged, Room: room));
        }
    }

    // ---- incoming messages ----

    /// <summary>A room message: accepted only for a room this machine knows, from an agent that was a member when it wrote it.</summary>
    private void OnRoomMessage(StoreTransaction tx, Envelope envelope, MessageContent content, Guid roomId)
    {
        var sender = $"{envelope.FromMachine}/{content.FromAgent}";
        var record = tx.GetRoom(roomId);
        var view = record is null ? null : TryView(record);
        var skew = _messaging.CurrentValue.ClockSkew;
        var why = view switch
        {
            null => "a room this machine doesn't know",
            _ when content.ConversationId != roomId || content.ToAgent != RoomAddress.Agent => "malformed",
            _ when !view.History.Any(h => h.Member == sender && Within(h, content.CreatedOn, skew)) => $"{Display(sender)} wasn't a member when it wrote it",
            _ => null,
        };
        if (why is null)
        {
            tx.AddEvent(envelope.FromMachine, envelope.Id, MessageDirection.In, RoomAddress.Agent, MessageEvents.Received);
        }
        else
        {
            tx.AddEvent(envelope.FromMachine, envelope.Id, MessageDirection.In, RoomAddress.Agent, MessageEvents.Rejected, why);
            tx.Diagnose("warning", "room", $"ignored a room message: {why}", envelope.FromMachine, envelope.Id);
        }
    }

    /// <summary>Pushes a room message to the local agents that were members when it was written (but its sender).</summary>
    private void PushToRoom(RoomRecord record, StoredMessage stored, MessageDirection direction, string? exceptAgent = null)
    {
        if (TryView(record) is not { } view)
        {
            return;
        }

        var local = view.History.Where(h => Within(h, stored.CreatedOn) && h.LeftOn is null).Select(h => Address.Parse(h.Member))
            .Where(a => a.Machine == MachineId && a.Agent != exceptAgent).Select(a => a.Agent!).Distinct().ToList();
        if (local.Count == 0)
        {
            return;
        }

        var message = ToLocal(stored, direction);
        foreach (var agent in local)
        {
            _sessions.Get(agent)?.Push(new LocalEvent(LocalEventKinds.RoomMessage, Message: message));
        }
    }

    // ---- helpers ----

    private async Task<LocalRoom> InviteAsync(string agent, string room, IReadOnlyList<string> invitees, CancellationToken cancellationToken)
    {
        var record = await FreshRoomAsync(room, cancellationToken);
        RequireMember(record, agent);
        var view = View(record);
        var open = RoomLog.Open(view, Now).Select(i => i.Invitee).ToHashSet();
        foreach (var invitee in invitees.Where(i => !view.Members.ContainsKey(i) && !open.Contains(i)).Distinct())
        {
            await SubmitAsync(new RoomEvent(Guid.NewGuid(), record.Id, RoomEventKind.Invite, Me(agent), invitee, null, null, Now,
                Now + _options.CurrentValue.RoomInviteLifetime, "", ""), cancellationToken);
        }

        return ToLocalRoom(RequireRoom(record.Id.ToString()));
    }

    private async Task SubmitAsync(RoomEvent e, CancellationToken cancellationToken) =>
        ApplyRoom(await _relay.SubmitRoomEventAsync(_relay.SignRoomEvent(e), cancellationToken));

    /// <summary>The room as the router has it now (rooms need the router).</summary>
    private async Task<RoomRecord> FreshRoomAsync(string room, CancellationToken cancellationToken)
    {
        var record = RequireRoom(room);
        ApplyRoom(await _relay.GetRoomAsync(record.Id, cancellationToken));
        record = Store.GetRoom(record.Id)!;
        return record.Collapsed ? throw new InvalidOperationException($"{record.Name} has collapsed ({record.CollapseReason})") : record;
    }

    private void RequireMember(RoomRecord record, string agent)
    {
        if (!View(record).Members.ContainsKey(Me(agent)))
        {
            throw new InvalidOperationException($"{agent} isn't in {record.Name}");
        }
    }

    /// <summary>Plain addresses (groups resolved) as <c>machineId/agent</c>.</summary>
    private async Task<IReadOnlyList<string>> InviteeIdsAsync(IReadOnlyCollection<string> addresses, string from, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var text in await ResolveAddressesAsync(addresses, from, cancellationToken))
        {
            var address = Address.Parse(text);
            if (address.Agent is null or Address.DaemonAgent or RoomAddress.Agent)
            {
                throw new ArgumentException($"'{text}' can't be invited: invite machine/agent");
            }

            var machine = address.Machine == MachineId || address.Machine == _relay.Name ? MachineId : (await _relay.ResolveAsync(address.Machine, cancellationToken)).Id;
            ids.Add($"{machine}/{address.Agent}");
        }

        return ids.Count > Limits.MaxRoomMembers ? throw new ArgumentException($"a room has at most {Limits.MaxRoomMembers} members") : ids;
    }

    private RoomView View(RoomRecord record) => RoomLog.Replay(record.Events, IsApprovedMachine, RoomLog.AlreadyChecked); // checked when stored

    private RoomView? TryView(RoomRecord record)
    {
        try
        {
            return View(record);
        }
        catch (RoomLogException)
        {
            return null;
        }
    }

    /// <summary>A removed machine is no longer in the router's list (a router <c>removed</c> event is accepted only then).</summary>
    private bool IsApprovedMachine(string machineId) => machineId == MachineId || _relay.Machine(machineId) is not null;

    private static bool Within(RoomMembership span, DateTimeOffset on, TimeSpan skew = default) =>
        on >= span.JoinedOn - skew && (span.LeftOn is not { } left || on <= left + skew);

    private string Me(string agent) => $"{MachineId}/{agent}";

    private string OwnName => _relay.Name ?? NameOf(MachineId);

    private string Display(string member)
    {
        var address = Address.Parse(member);
        return $"{(address.Machine == MachineId ? OwnName : NameOf(address.Machine))}/{address.Agent}";
    }
}
