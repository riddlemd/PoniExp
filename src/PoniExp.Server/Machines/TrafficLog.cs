using System.Net;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Machines;

/// <summary>
/// The router's traffic log (see docs/plan.md, Traffic log): off unless the owner turns it on
/// (<c>poniexp admin traffic-log on</c>; the switch survives restarts), and it records only what the router can
/// see. Per message: which machines, the id, size and sequence, and what happened to it. Refusals carry their
/// reason and the connection's address, so delivery delays and attacks show. Kept for
/// <see cref="RouterOptions.TrafficLogRetention"/>.
/// </summary>
public sealed class TrafficLog(RouterStore store, IOptionsMonitor<RouterOptions> options, TimeProvider time, ILogger<TrafficLog> logger)
{
    private const string EnabledState = "traffic-log";

    private volatile bool _enabled = store.GetState(EnabledState) == "on";

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        store.SetState(EnabledState, enabled ? "on" : "off");
        _enabled = enabled;
        logger.LogInformation("Traffic log {State}", enabled ? "on" : "off");
    }

    /// <summary>The router took the envelope from its sender.</summary>
    public void Relayed(Envelope e) => Message(TrafficEvents.Relayed, e);

    /// <summary>The router handed the envelope to a connection of its recipient (pushed, or on reconnect).</summary>
    public void Delivered(Envelope e) => Message(TrafficEvents.Delivered, e);

    /// <summary>The recipient stored it, and the router dropped its copy.</summary>
    public void Confirmed(Envelope e) => Message(TrafficEvents.Confirmed, e);

    /// <summary>It expired before its recipient confirmed it.</summary>
    public void Expired(Envelope e) => Message(TrafficEvents.Expired, e);

    /// <summary>Dropped because the sender or recipient was removed.</summary>
    public void Dropped(Envelope e, string why) => Message(TrafficEvents.Dropped, e, why);

    /// <summary>Something refused: a message, a connection, an authentication, a join request.</summary>
    public void Refused(string reason, IPAddress? address, string? machine = null, Envelope? envelope = null) =>
        Append(new TrafficEntry(time.GetUtcNow(), TrafficEvents.Refused, envelope?.FromMachine ?? machine, envelope?.ToMachine, envelope?.Id,
            envelope?.Size, envelope?.Sequence, Normalize(address), reason));

    /// <summary>A new join request was accepted (pending approval).</summary>
    public void JoinRequested(string machine, string name, IPAddress? address) =>
        Append(new TrafficEntry(time.GetUtcNow(), TrafficEvents.JoinRequested, machine, null, null, null, null, Normalize(address), name));

    public IReadOnlyList<TrafficEntry> Read(DateTimeOffset? since, string? machine, int limit) => store.ReadTraffic(since, machine, limit);

    public int Prune() => store.PruneTraffic(time.GetUtcNow() - options.CurrentValue.TrafficLogRetention);

    private void Message(string @event, Envelope e, string? detail = null) =>
        Append(new TrafficEntry(time.GetUtcNow(), @event, e.FromMachine, e.ToMachine, e.Id, e.Size, e.Sequence, null, detail));

    private void Append(TrafficEntry entry)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            store.AppendTraffic(entry);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            logger.LogWarning(e, "Couldn't write the traffic log");
        }
    }

    private static string? Normalize(IPAddress? address) => (address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address)?.ToString();
}

public static class TrafficEvents
{
    public const string Relayed = "relayed";
    public const string Delivered = "delivered";
    public const string Confirmed = "confirmed";
    public const string Expired = "expired";
    public const string Dropped = "dropped";
    public const string Refused = "refused";
    public const string JoinRequested = "join-requested";
}

/// <summary>One traffic log row. Machines are ids; <see cref="Address"/> is set only for refusals and join requests.</summary>
public sealed record TrafficEntry(DateTimeOffset OccurredOn, string Event, string? FromMachine, string? ToMachine, Guid? MessageId, int? Size,
    long? Sequence, string? Address, string? Detail);
