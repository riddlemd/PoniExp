using System.Security.Cryptography;
using System.Text;
using PoniExp.Core.Crypto;

namespace PoniExp.Core.Protocol;

public enum RoomEventKind
{
    /// <summary>Opens the room; its author is the first member. The room id is derived from it.</summary>
    Create,
    /// <summary>A member invites an agent (<see cref="RoomEvent.Subject"/>), until <see cref="RoomEvent.ExpiresOn"/>.</summary>
    Invite,
    /// <summary>The invitee accepts (<see cref="RoomEvent.InviteId"/>): it is a member from here on.</summary>
    Join,
    /// <summary>A member leaves.</summary>
    Leave,
    /// <summary>The router's own: the owner removed machine <see cref="RoomEvent.Subject"/>, so its agents are out. Unsigned.</summary>
    Removed,
}

/// <summary>
/// One entry in a room's membership log (see docs/plan.md, Rooms). Signed by its author's machine key over every
/// field; <see cref="ActorKey"/> is that key, whose fingerprint must be the actor's machine id, so a log can be
/// checked without asking anyone (even after a machine is removed). <see cref="Actor"/> and <see cref="Subject"/>
/// are <c>machineId/agent</c> (Subject is a machine id for <see cref="RoomEventKind.Removed"/>).
/// </summary>
public sealed record RoomEvent(Guid EventId, Guid RoomId, RoomEventKind Kind, string Actor, string? Subject, string? Name, Guid? InviteId,
    DateTimeOffset OccurredOn, DateTimeOffset? ExpiresOn, string ActorKey, string Signature);

/// <summary>A collapsed room, as the router keeps it: counts and timings only (it never has subjects or bodies).</summary>
public sealed record RoomStats(Guid RoomId, string Name, DateTimeOffset OpenedOn, DateTimeOffset CollapsedOn, string Reason,
    IReadOnlyList<RoomMembership> Members, IReadOnlyList<RoomMachineStats> Machines, IReadOnlyList<RoomMessageStat> Messages);

/// <summary>Messages sent into a room from one machine (the sending agent is end-to-end encrypted, so stats are per machine).</summary>
public sealed record RoomMachineStats(string Machine, int Messages, long Bytes);

public sealed record RoomMessageStat(DateTimeOffset SentOn, string FromMachine, long Size);

/// <summary>What the router tells the owner about a live room.</summary>
public sealed record RoomSummary(Guid RoomId, string Name, DateTimeOffset OpenedOn, DateTimeOffset LastActivityOn, IReadOnlyList<string> Members,
    IReadOnlyList<string> Invited, int Messages);

/// <summary>A room as the router serves it: its log, or (once collapsed) the log it ended with.</summary>
public sealed record RoomState(Guid RoomId, string Name, IReadOnlyList<RoomEvent> Events, bool Collapsed = false, string? CollapseReason = null);

public sealed record RoomInvitation(Guid InviteId, string Invitee, string Inviter, DateTimeOffset ExpiresOn);

public sealed record RoomMembership(string Member, DateTimeOffset JoinedOn, DateTimeOffset? LeftOn);

/// <summary>What a valid log says: who is in the room, who is invited, and everyone who ever was.</summary>
public sealed record RoomView(Guid RoomId, string Name, string Creator, DateTimeOffset OpenedOn, IReadOnlyDictionary<string, DateTimeOffset> Members,
    IReadOnlyList<RoomInvitation> Invitations, IReadOnlyList<RoomMembership> History, DateTimeOffset LastEventOn)
{
    public IReadOnlyList<string> MemberMachines => [.. Members.Keys.Select(m => Address.Parse(m).Machine).Distinct()];

    public bool Involves(string machineId) =>
        History.Any(h => Address.Parse(h.Member).Machine == machineId) || Invitations.Any(i => Address.Parse(i.Invitee).Machine == machineId);
}

public sealed class RoomLogException(string message) : Exception(message);

/// <summary>Why the router refused a room message whose key isn't wrapped for exactly the current members' machines.</summary>
public static class RoomRefusals
{
    public const string MembersChanged = "The room's members changed";
    public const string Collapsed = "The room has collapsed";
}

/// <summary>
/// Checks a room's log from its <c>create</c> on, the same way on the router and on every member's daemon. Every
/// event must be signed by its author's machine and allowed at its place: an invite only from a member, a join only
/// by the invitee of a live invite (each invite used once), a leave only from a member.
/// </summary>
public static class RoomLog
{
    /// <summary>For replaying a log that was checked when it was stored: no join is refused for lateness.</summary>
    public static readonly TimeSpan AlreadyChecked = TimeSpan.FromDays(3650);

    /// <summary>The longest an invite may stay open.</summary>
    public static readonly TimeSpan MaxInviteLifetime = TimeSpan.FromDays(30);

    /// <summary>A room id: derived from its <c>create</c> event, so no one can claim an existing room's id for another log.</summary>
    public static Guid RoomIdOf(RoomEvent create)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"poniexp-room\n{create.EventId:D}\n{create.Actor}\n{create.Name}\n{Signatures.Stamp(create.OccurredOn)}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <param name="isApproved">Whether a machine is approved (a router <c>removed</c> event is accepted only for one that isn't).</param>
    /// <param name="skew">How far a join may come after its invite expired.</param>
    /// <exception cref="RoomLogException">The first invalid event, and why.</exception>
    public static RoomView Replay(IReadOnlyList<RoomEvent> events, Func<string, bool> isApproved, TimeSpan skew)
    {
        if (events.Count == 0 || events[0] is not { Kind: RoomEventKind.Create } create)
        {
            throw new RoomLogException("a room's log starts with create");
        }

        if (create.RoomId != RoomIdOf(create) || !Limits.IsValidAgent(create.Name ?? ""))
        {
            throw new RoomLogException("the create event doesn't fit its room id, or its name isn't valid");
        }

        var members = new Dictionary<string, DateTimeOffset>();
        var invitations = new List<RoomInvitation>();
        var history = new List<RoomMembership>();
        var seen = new HashSet<Guid>();
        foreach (var e in events)
        {
            string Fail(string why) => throw new RoomLogException($"{WireProtocol.NameOf(e.Kind)} {e.EventId.ToString()[..8]} by {e.Actor}: {why}");

            if (e.RoomId != create.RoomId || !seen.Add(e.EventId))
            {
                Fail("another room's event, or a repeat");
            }

            if (e.Kind == RoomEventKind.Removed)
            {
                if (e.Subject is not { } machine || isApproved(machine))
                {
                    Fail("only a removed machine's agents can be removed");
                }

                foreach (var member in members.Keys.Where(m => Address.Parse(m).Machine == e.Subject).ToList())
                {
                    Depart(member, e.OccurredOn);
                }

                invitations.RemoveAll(i => Address.Parse(i.Invitee).Machine == e.Subject);
                continue;
            }

            Address actor;
            try
            {
                actor = Address.Parse(e.Actor);
            }
            catch (FormatException)
            {
                Fail("not machine/agent");
                continue;
            }

            if (!IsSignedBy(e, actor.Machine))
            {
                Fail("not signed by its author's machine");
            }

            switch (e.Kind)
            {
                case RoomEventKind.Create when ReferenceEquals(e, create):
                    members[e.Actor] = e.OccurredOn;
                    history.Add(new RoomMembership(e.Actor, e.OccurredOn, null));
                    break;
                case RoomEventKind.Invite:
                    if (!members.ContainsKey(e.Actor))
                    {
                        Fail("only a member can invite");
                    }

                    if (e.Subject is not { } invitee || !IsMemberAddress(invitee) || members.ContainsKey(invitee))
                    {
                        Fail("invites a member, or not machine/agent");
                    }

                    if (e.ExpiresOn is not { } expires || expires <= e.OccurredOn || expires - e.OccurredOn > MaxInviteLifetime)
                    {
                        Fail($"an invite lasts up to {MaxInviteLifetime.TotalDays:0} days");
                    }

                    invitations.Add(new RoomInvitation(e.EventId, e.Subject!, e.Actor, e.ExpiresOn!.Value));
                    break;
                case RoomEventKind.Join:
                    var invitation = invitations.FirstOrDefault(i => i.InviteId == e.InviteId);
                    if (invitation is null || invitation.Invitee != e.Actor)
                    {
                        Fail("not the invitee of an open invite");
                    }

                    if (e.OccurredOn > invitation!.ExpiresOn + skew)
                    {
                        Fail("the invite had expired");
                    }

                    if (members.Count >= Limits.MaxRoomMembers)
                    {
                        Fail($"a room has at most {Limits.MaxRoomMembers} members");
                    }

                    invitations.RemoveAll(i => i.Invitee == e.Actor); // every invite of theirs is used up
                    members[e.Actor] = e.OccurredOn;
                    history.Add(new RoomMembership(e.Actor, e.OccurredOn, null));
                    break;
                case RoomEventKind.Leave:
                    if (!members.ContainsKey(e.Actor))
                    {
                        Fail("only a member can leave");
                    }

                    Depart(e.Actor, e.OccurredOn);
                    break;
                default:
                    Fail("a second create");
                    break;
            }
        }

        return new RoomView(create.RoomId, create.Name!, create.Actor, create.OccurredOn, members, invitations, history,
            events.Max(e => e.OccurredOn));

        void Depart(string member, DateTimeOffset on)
        {
            members.Remove(member);
            var last = history.FindLastIndex(h => h.Member == member && h.LeftOn is null);
            if (last >= 0)
            {
                history[last] = history[last] with { LeftOn = on };
            }
        }
    }

    public static bool IsSignedBy(RoomEvent e, string machineId)
    {
        try
        {
            return KeyFingerprint.MachineIdOf(e.ActorKey) == machineId && PublicKeys.Verify(e.ActorKey, Signatures.RoomEventPayload(e), e.Signature);
        }
        catch (Exception x) when (x is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Invitations still open at <paramref name="now"/>.</summary>
    public static IEnumerable<RoomInvitation> Open(RoomView view, DateTimeOffset now) => view.Invitations.Where(i => i.ExpiresOn > now);

    public static bool IsMemberAddress(string text)
    {
        try
        {
            var address = Address.Parse(text);
            return address.Agent is not (Address.DaemonAgent or RoomAddress.Agent);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
