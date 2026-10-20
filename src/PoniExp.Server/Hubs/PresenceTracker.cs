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
            lock (_lock)
            {
                if (_holding.TryGetValue(context.ConnectionId, out var held))
                {
                    _holding[context.ConnectionId] = held with { Pending = true }; // dropped once its call has answered
                    continue;
                }
            }

            context.Abort();
        }
    }

    private readonly Dictionary<string, (HubCallerContext Context, bool Pending)> _holding = [];

    /// <summary>
    /// Holds off dropping this connection while it runs a call (an admin command that removes or revokes its own
    /// machine), so the caller gets its answer; a drop asked for meanwhile happens shortly after.
    /// </summary>
    public IAsyncDisposable Hold(HubCallerContext context)
    {
        lock (_lock)
        {
            _holding[context.ConnectionId] = (context, false);
        }

        return new Release(this, context);
    }

    private sealed class Release(PresenceTracker tracker, HubCallerContext context) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            bool pending;
            lock (tracker._lock)
            {
                pending = tracker._holding.Remove(context.ConnectionId, out var held) && held.Pending;
            }

            if (pending)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1)); // the call's result is written after the method returns
                    context.Abort();
                });
            }

            await Task.CompletedTask;
        }
    }
}
