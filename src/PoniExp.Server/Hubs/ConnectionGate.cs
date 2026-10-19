using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace PoniExp.Server.Hubs;

/// <summary>
/// Admission control for the relay port, by what each connection has proved (see docs/plan.md,
/// Connection limits). A new connection is unauthenticated: it must prove a key that is approved or
/// pending within <see cref="RouterOptions.AuthenticationTimeout"/>, and only so many may wait at once.
/// A machine may hold only so many connections, and only so many new join requests are accepted per hour.
/// Per-address limits apply on top, except to <see cref="RouterOptions.SharedAddresses"/>, which stand for
/// many clients (behind port forwarding, every client has the forwarder's address).
/// </summary>
public sealed class ConnectionGate(IOptionsMonitor<RouterOptions> options, TimeProvider time, ILogger<ConnectionGate> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _connections = [];
    private readonly Queue<(DateTimeOffset On, IPAddress? Address)> _joins = new();

    /// <summary>Admits a new, unauthenticated connection, or says why not.</summary>
    public string? Open(HubCallerContext context, IPAddress? address)
    {
        var settings = options.CurrentValue;
        address = Normalize(address);
        lock (_lock)
        {
            var waiting = _connections.Values.Where(e => e.Machine is null).ToList();
            if (waiting.Count >= settings.MaxUnauthenticatedConnections)
            {
                return "too many connections are waiting to authenticate";
            }

            if (!IsShared(address, settings) && waiting.Count(e => Equals(e.Address, address)) >= settings.MaxUnauthenticatedPerAddress)
            {
                return $"too many connections from {address} are waiting to authenticate";
            }

            var entry = new Entry(context, address);
            entry.Deadline = time.CreateTimer(_ => Expire(context.ConnectionId), null, settings.AuthenticationTimeout, Timeout.InfiniteTimeSpan);
            _connections[context.ConnectionId] = entry;
            return null;
        }
    }

    /// <summary>
    /// The connection proved a key that is approved or pending: it no longer has a deadline. Refused (with
    /// why) if that machine already holds as many connections as it may.
    /// </summary>
    public string? Admit(string connectionId, string machineId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(connectionId, out var entry))
            {
                return "this connection has closed";
            }

            if (entry.Machine == machineId)
            {
                return null;
            }

            if (_connections.Values.Count(e => e.Machine == machineId) >= options.CurrentValue.MaxConnectionsPerMachine)
            {
                return "this machine already has as many connections as it may hold";
            }

            entry.Machine = machineId;
            entry.Deadline?.Dispose();
            entry.Deadline = null;
            return null;
        }
    }

    /// <summary>Takes one of the hour's new join requests, or says why there are none left.</summary>
    public string? TakeJoin(IPAddress? address)
    {
        var settings = options.CurrentValue;
        address = Normalize(address);
        var now = time.GetUtcNow();
        lock (_lock)
        {
            while (_joins.TryPeek(out var oldest) && now - oldest.On > TimeSpan.FromHours(1))
            {
                _joins.Dequeue();
            }

            if (_joins.Count >= settings.JoinRequestsPerHour)
            {
                return "the router has had too many join requests this hour; try again later";
            }

            if (!IsShared(address, settings) && _joins.Count(j => Equals(j.Address, address)) >= settings.JoinRequestsPerHourPerAddress)
            {
                return $"too many join requests from {address} this hour; try again later";
            }

            _joins.Enqueue((now, address));
            return null;
        }
    }

    /// <summary>Closes every connection that proved <paramref name="machineId"/> (its join request expired or was cleared).</summary>
    public void Expel(string machineId)
    {
        List<HubCallerContext> contexts;
        lock (_lock)
        {
            contexts = [.. _connections.Values.Where(e => e.Machine == machineId).Select(e => e.Context)];
        }

        foreach (var context in contexts)
        {
            context.Abort();
        }
    }

    public void Close(string connectionId)
    {
        lock (_lock)
        {
            if (_connections.Remove(connectionId, out var entry))
            {
                entry.Deadline?.Dispose();
            }
        }
    }

    /// <summary>Connections that haven't proved an approved or pending key (for tests and health).</summary>
    public int Unauthenticated
    {
        get
        {
            lock (_lock)
            {
                return _connections.Values.Count(e => e.Machine is null);
            }
        }
    }

    private void Expire(string connectionId)
    {
        HubCallerContext? context = null;
        lock (_lock)
        {
            if (_connections.TryGetValue(connectionId, out var entry) && entry.Machine is null)
            {
                context = entry.Context;
            }
        }

        if (context is not null)
        {
            logger.LogInformation("Closing connection {Id}: it didn't authenticate in time", connectionId);
            context.Abort();
        }
    }

    private static IPAddress? Normalize(IPAddress? address) => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    private static bool IsShared(IPAddress? address, RouterOptions settings) =>
        address is null || settings.SharedAddresses.Any(s => IPAddress.TryParse(s, out var shared) && Normalize(shared)!.Equals(address));

    private sealed class Entry(HubCallerContext context, IPAddress? address)
    {
        public HubCallerContext Context { get; } = context;

        public IPAddress? Address { get; } = address;

        /// <summary>The approved or pending machine this connection proved; null while it hasn't.</summary>
        public string? Machine { get; set; }

        public ITimer? Deadline { get; set; }
    }
}
