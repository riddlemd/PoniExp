using Microsoft.AspNetCore.SignalR;

namespace PoniExp.Server.Hubs;

/// <summary>Which approved machines have a live connection. A machine may hold several (a listener and a CLI).</summary>
public sealed class PresenceTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string MachineId, HubCallerContext Context)> _connections = [];

    /// <summary>Adds a connection. True if this machine just came online.</summary>
    public bool Add(string machineId, HubCallerContext context)
    {
        lock (_lock)
        {
            var wasOnline = _connections.Values.Any(c => c.MachineId == machineId);
            _connections[context.ConnectionId] = (machineId, context);
            return !wasOnline;
        }
    }

    /// <summary>Removes a connection. True (with the machine id) if its machine just went offline.</summary>
    public bool Remove(string connectionId, out string? machineId)
    {
        lock (_lock)
        {
            if (!_connections.Remove(connectionId, out var entry))
            {
                machineId = null;
                return false;
            }

            machineId = entry.MachineId;
            return _connections.Values.All(c => c.MachineId != entry.MachineId);
        }
    }

    public bool IsOnline(string machineId)
    {
        lock (_lock)
        {
            return _connections.Values.Any(c => c.MachineId == machineId);
        }
    }

    /// <summary>Drops every connection of a machine (used when it is removed or rejected).</summary>
    public void Disconnect(string machineId)
    {
        List<HubCallerContext> contexts;
        lock (_lock)
        {
            contexts = _connections.Values.Where(c => c.MachineId == machineId).Select(c => c.Context).ToList();
        }

        foreach (var context in contexts)
        {
            context.Abort();
        }
    }
}
