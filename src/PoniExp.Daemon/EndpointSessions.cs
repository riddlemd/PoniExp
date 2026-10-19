using System.Collections.Concurrent;
using System.Threading.Channels;
using PoniExp.Client;

namespace PoniExp.Daemon;

/// <summary>
/// The endpoints connected to this daemon right now: one live stream each, with its last heartbeat. The
/// registry of every endpoint ever registered is the <c>endpoints</c> table; this is who is online.
/// </summary>
public sealed class EndpointSessions
{
    private readonly ConcurrentDictionary<string, EndpointSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>A new session for <paramref name="name"/>, or null if another process holds it.</summary>
    public EndpointSession? TryOpen(string name, DateTimeOffset now)
    {
        var session = new EndpointSession(name, now);
        return _sessions.TryAdd(name, session) ? session : null;
    }

    public void Close(EndpointSession session) => _sessions.TryRemove(new KeyValuePair<string, EndpointSession>(session.Name, session));

    public EndpointSession? Get(string name) => _sessions.GetValueOrDefault(name);

    public bool IsOnline(string name) => _sessions.ContainsKey(name);

    public IReadOnlyCollection<EndpointSession> All => [.. _sessions.Values];

    /// <summary>Ends the sessions that missed their heartbeat; their streams record them as timed out.</summary>
    public IReadOnlyList<EndpointSession> TimeOut(DateTimeOffset now, TimeSpan timeout)
    {
        var due = _sessions.Values.Where(s => now - s.LastHeartbeat > timeout).ToList();
        foreach (var session in due)
        {
            session.End(leaving: false);
        }

        return due;
    }
}

public sealed class EndpointSession(string name, DateTimeOffset openedOn)
{
    private readonly Channel<LocalEvent> _events = Channel.CreateUnbounded<LocalEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _ended = new();

    public string Name { get; } = name;

    public DateTimeOffset LastHeartbeat { get; private set; } = openedOn;

    /// <summary>True when the endpoint unregistered cleanly (it shows as left, not timed out).</summary>
    public bool Leaving { get; private set; }

    public CancellationToken Ended => _ended.Token;

    public ChannelReader<LocalEvent> Events => _events.Reader;

    public void Beat(DateTimeOffset now) => LastHeartbeat = now;

    public bool Push(LocalEvent e) => _events.Writer.TryWrite(e);

    public void End(bool leaving)
    {
        Leaving |= leaving;
        _events.Writer.TryComplete();
        _ended.Cancel();
    }
}
