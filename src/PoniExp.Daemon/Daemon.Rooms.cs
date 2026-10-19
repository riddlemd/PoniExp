using System.Text.Json;
using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// Rooms (see docs/plan.md, Phase 7). Membership is a versioned snapshot every member's daemon keeps, replaced
/// only by a newer one from an admin (or by a member leaving); the router knows nothing of it. A room message is
/// sealed once for every member machine and fanned out by the router; this daemon accepts one only from a current
/// member, and shows it to every local member agent. No receipts, no inbox: a room is read like a channel.
/// </summary>
public sealed partial class Daemon
{
    /// <summary>Creates a room with <paramref name="agent"/> as its first admin and member, and tells every member machine.</summary>
    internal async Task<RoomSnapshot> CreateRoomAsync(string agent, string name, IReadOnlyCollection<string> members, CancellationToken cancellationToken)
    {
        if (name is not { Length: > 0 and <= Limits.MaxSubjectLength })
        {
            throw new ArgumentException($"a room name is 1-{Limits.MaxSubjectLength} characters");
        }

        var me = $"{MachineId}/{agent}";
        var snapshot = new RoomSnapshot(Guid.NewGuid(), name, 1, [me, .. (await MemberIdsAsync(members, agent, cancellationToken)).Where(m => m != me)], [me]);
        await PublishAsync(null, snapshot, agent);
        return snapshot;
    }

    /// <summary>An admin changes the room: adds or removes members and admins, renames or closes it.</summary>
    internal async Task<RoomSnapshot> ChangeRoomAsync(string agent, string room, RoomChange change, CancellationToken cancellationToken)
    {
        var current = RequireRoom(room).Snapshot;
        var me = $"{MachineId}/{agent}";
        if (change.Leave)
        {
            if (!current.Members.Contains(me))
            {
                throw new InvalidOperationException($"{agent} isn't a member of {current.Name}");
            }

            var left = current with { Version = current.Version + 1, Members = [.. current.Members.Where(m => m != me)], Admins = [.. current.Admins.Where(a => a != me)] };
            await PublishAsync(current, left, agent);
            return left;
        }

        if (!current.Admins.Contains(me))
        {
            throw new InvalidOperationException($"only an admin of {current.Name} can change it ({string.Join(", ", current.Admins.Select(Display))})");
        }

        var add = await MemberIdsAsync(change.Add ?? [], agent, cancellationToken);
        var remove = await MemberIdsAsync(change.Remove ?? [], agent, cancellationToken);
        var addAdmins = await MemberIdsAsync(change.AddAdmins ?? [], agent, cancellationToken);
        var removeAdmins = await MemberIdsAsync(change.RemoveAdmins ?? [], agent, cancellationToken);
        var members = current.Members.Concat(add).Concat(addAdmins).Except(remove).Distinct().ToList();
        var admins = current.Admins.Concat(addAdmins).Except(removeAdmins).Intersect(members).Distinct().ToList();
        if (admins.Count == 0 && change.Close != true)
        {
            throw new InvalidOperationException("a room needs an admin (or close it)");
        }

        var next = current with
        {
            Version = current.Version + 1,
            Name = change.Name ?? current.Name,
            Members = members,
            Admins = admins,
            Closed = change.Close ?? current.Closed,
        };
        await PublishAsync(current, next, agent);
        return next;
    }

    /// <summary>Sends a message to the room, sealed once for every member machine.</summary>
    internal async Task<SentMessage> SendToRoomAsync(string agent, string room, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var snapshot = RequireRoom(room).Snapshot;
        var me = $"{MachineId}/{agent}";
        if (!snapshot.Members.Contains(me) || snapshot.Closed)
        {
            throw new InvalidOperationException(snapshot.Closed ? $"{snapshot.Name} is closed" : $"{agent} isn't a member of {snapshot.Name}");
        }

        var machines = new List<MachineInfo>();
        foreach (var machine in snapshot.Members.Select(m => Address.Parse(m).Machine).Distinct().Where(m => m != MachineId))
        {
            machines.Add(await _relay.ResolveAsync(machine, cancellationToken));
        }

        var envelope = Store.Transact(Now, tx =>
        {
            var sealedMessage = _relay.ComposeRoom(tx, snapshot.RoomId, machines, message with { FromAgent = agent });
            if (machines.Count == 0)
            {
                tx.AddEvent(MachineId, sealedMessage.Id, MessageDirection.Out, agent, MessageEvents.Relayed, "every member is on this machine");
            }

            return sealedMessage;
        });
        var stored = Store.GetRoomMessages(MachineId, snapshot.RoomId, null).Single(m => m.Id == envelope.Id);
        PushToRoom(snapshot, stored, MessageDirection.Out, exceptAgent: agent);
        if (machines.Count == 0)
        {
            return new SentMessage(envelope.Id, envelope.ToMachine, RoomAddress.Agent, snapshot.RoomId, envelope.Sequence, envelope.ExpiresOn, null);
        }

        return (await _relay.RelayComposedAsync([envelope], cancellationToken))[0];
    }

    /// <summary>The room's messages as <paramref name="agent"/> (a member) sees them.</summary>
    internal IReadOnlyList<LocalMessage> ReadRoom(string agent, string room, DateTimeOffset? since, int limit)
    {
        var snapshot = RequireRoom(room).Snapshot;
        if (!snapshot.Members.Contains($"{MachineId}/{agent}"))
        {
            throw new InvalidOperationException($"{agent} isn't a member of {snapshot.Name}");
        }

        return [.. Store.GetRoomMessages(MachineId, snapshot.RoomId, since, limit)
            .Select(m => ToLocal(m, m.FromMachine == MachineId ? MessageDirection.Out : MessageDirection.In))];
    }

    internal IReadOnlyList<LocalRoom> Rooms(string? agent) =>
        [.. Store.GetRooms().Select(r => r.Snapshot)
            .Where(s => agent is null ? s.Members.Any(m => Address.Parse(m).Machine == MachineId) : s.Members.Contains($"{MachineId}/{agent}"))
            .Select(ToLocalRoom)];

    internal LocalRoom ToLocalRoom(RoomSnapshot s) =>
        new(s.RoomId, s.Name, s.Version, [.. s.Members.Select(Display)], [.. s.Admins.Select(Display)], s.Closed);

    internal RoomRecord RequireRoom(string room)
    {
        if (Guid.TryParse(room, out var id) && Store.GetRoom(id) is { } byId)
        {
            return byId;
        }

        var named = Store.GetRooms().Where(r => r.Snapshot.Name == room || r.Snapshot.RoomId.ToString().StartsWith(room, StringComparison.Ordinal)).ToList();
        return named.Count switch
        {
            1 => named[0],
            0 => throw new KeyNotFoundException($"no room '{room}' on this machine"),
            _ => throw new ArgumentException($"'{room}' matches {named.Count} rooms; use its id"),
        };
    }

    // ---- incoming ----

    /// <summary>A room message: accepted only for a room this machine knows, from one of its current members.</summary>
    private void OnRoomMessage(StoreTransaction tx, Envelope envelope, MessageContent content, Guid roomId)
    {
        var sender = $"{envelope.FromMachine}/{content.FromAgent}";
        var why = tx.GetRoom(roomId)?.Snapshot switch
        {
            null => "a room this machine doesn't know",
            { Closed: true } => "a closed room",
            var s when !s.Members.Contains(sender) => $"{Display(sender)} isn't a member",
            var s when s.Members.All(m => Address.Parse(m).Machine != MachineId) => "no member here any more",
            _ when content.ConversationId != roomId || content.ToAgent != RoomAddress.Agent => "malformed",
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

    /// <summary>
    /// A membership snapshot. Applied only if newer, from an admin of the room as this machine knows it (or a member
    /// leaving); a room seen for the first time must name its sender as an admin. The router can't forge one.
    /// </summary>
    private void OnRoomSnapshot(StoreTransaction tx, Envelope envelope, MessageContent content)
    {
        var changer = $"{envelope.FromMachine}/{content.FromAgent}";
        if (Parse<RoomSnapshot>(content.Body) is not { } next || !IsWellFormed(next))
        {
            tx.Diagnose("warning", "room", "a malformed room snapshot", envelope.FromMachine, envelope.Id);
            return;
        }

        var current = tx.GetRoom(next.RoomId)?.Snapshot;
        var why = current switch
        {
            null when !next.Admins.Contains(changer) => $"{Display(changer)} isn't an admin of a room this machine didn't know",
            null => null,
            _ when next.Version <= current.Version => null, // old news (a replay, or a duplicate): ignore quietly
            { Closed: true } => "the room is closed",
            _ when current.Admins.Contains(changer) => null,
            _ when IsLeaving(current, next, changer) => null,
            _ => $"{Display(changer)} isn't an admin",
        };
        if (why is not null)
        {
            tx.Diagnose("warning", "room", $"ignored a change to room {next.Name}: {why}", envelope.FromMachine, envelope.Id);
        }
        else if (current is null || next.Version > current.Version)
        {
            tx.SaveRoom(next, changer);
        }
    }

    private static bool IsLeaving(RoomSnapshot current, RoomSnapshot next, string changer) =>
        next.Version == current.Version + 1 && current.Members.Contains(changer) && next.Name == current.Name && next.Closed == current.Closed
        && next.Members.Order().SequenceEqual(current.Members.Where(m => m != changer).Order())
        && next.Admins.Order().SequenceEqual(current.Admins.Where(a => a != changer).Order());

    private static bool IsWellFormed(RoomSnapshot s)
    {
        try
        {
            return s.RoomId != Guid.Empty && s.Version > 0 && s.Name is { Length: > 0 and <= Limits.MaxSubjectLength }
                   && s.Members.Count <= Limits.MaxRoomMembers && s.Members.Distinct().Count() == s.Members.Count
                   && s.Members.All(m => Address.Parse(m).Agent is { } e && e != Address.DaemonAgent && e != RoomAddress.Agent)
                   && s.Admins.All(s.Members.Contains);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ---- helpers ----

    /// <summary>Saves the snapshot and sends it to the daemon of every machine with a member before or after (not this one).</summary>
    private async Task PublishAsync(RoomSnapshot? before, RoomSnapshot next, string agent)
    {
        var machines = (before?.Members ?? []).Concat(next.Members).Select(m => Address.Parse(m).Machine).Distinct().Where(m => m != MachineId).ToList();
        var targets = new List<MachineInfo>();
        foreach (var machine in machines)
        {
            targets.Add(await _relay.ResolveAsync(machine));
        }

        Store.Transact(Now, tx =>
        {
            tx.SaveRoom(next, $"{MachineId}/{agent}");
            foreach (var target in targets)
            {
                _relay.Compose(tx, target, Address.DaemonAgent, new OutgoingMessage(JsonSerializer.Serialize(next, WireProtocol.Json))
                {
                    FromAgent = agent, // signed with this machine's key: which agent made the change
                    Type = MessageType.Room,
                    ContentType = "application/json",
                });
            }
        });
        await FlushQuietlyAsync();
    }

    /// <summary>Plain addresses (roles and groups resolved) as <c>machineId/agent</c>.</summary>
    private async Task<IReadOnlyList<string>> MemberIdsAsync(IReadOnlyCollection<string> addresses, string from, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var text in await ResolveAddressesAsync(addresses, from, cancellationToken))
        {
            var address = Address.Parse(text);
            if (address.Agent is Address.DaemonAgent or RoomAddress.Agent)
            {
                throw new ArgumentException($"'{address.Agent}' can't be a room member");
            }

            var machine = address.Machine == MachineId || address.Machine == _relay.Name ? MachineId : (await _relay.ResolveAsync(address.Machine, cancellationToken)).Id;
            ids.Add($"{machine}/{address.Agent}");
        }

        return ids.Count + 0 > Limits.MaxRoomMembers ? throw new ArgumentException($"a room has at most {Limits.MaxRoomMembers} members") : ids;
    }

    /// <summary>Pushes a room message to every local member agent that's online (but its sender).</summary>
    private void PushToRoom(RoomSnapshot room, StoredMessage stored, MessageDirection direction, string? exceptAgent = null)
    {
        var local = room.Members.Select(Address.Parse).Where(a => a.Machine == MachineId && a.Agent != exceptAgent).ToList();
        if (local.Count == 0)
        {
            return;
        }

        var message = ToLocal(stored, direction);
        foreach (var member in local)
        {
            _sessions.Get(member.Agent)?.Push(new LocalEvent(LocalEventKinds.RoomMessage, Message: message));
        }
    }

    private string Display(string member)
    {
        var address = Address.Parse(member);
        return $"{(address.Machine == MachineId ? _relay.Name ?? NameOf(address.Machine) : NameOf(address.Machine))}/{address.Agent}";
    }
}
