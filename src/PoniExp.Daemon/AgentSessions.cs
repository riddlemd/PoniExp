using System.Collections.Concurrent;
using System.Threading.Channels;
using PoniExp.Client;

namespace PoniExp.Daemon;

/// <summary>
/// The agents connected to this daemon right now: one live stream each, with its last heartbeat. The
/// registry of every agent ever registered is the <c>agents</c> table; this is who is online.
/// </summary>
public sealed class AgentSessions
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>A new session for <paramref name="name"/>, or null if another process holds it.</summary>
    public AgentSession? TryOpen(string name, DateTimeOffset now)
    {
        var session = new AgentSession(name, now);
        return _sessions.TryAdd(name, session) ? session : null;
    }

    public void Close(AgentSession session) => _sessions.TryRemove(new KeyValuePair<string, AgentSession>(session.Name, session));

    public AgentSession? Get(string name) => _sessions.GetValueOrDefault(name);

    public bool IsOnline(string name) => _sessions.ContainsKey(name);

    public IReadOnlyCollection<AgentSession> All => [.. _sessions.Values];

    /// <summary>Ends the sessions that missed their heartbeat; their streams record them as timed out.</summary>
    public IReadOnlyList<AgentSession> TimeOut(DateTimeOffset now, TimeSpan timeout)
    {
        var due = _sessions.Values.Where(s => now - s.LastHeartbeat > timeout).ToList();
        foreach (var session in due)
        {
            session.End(leaving: false);
        }

        return due;
    }
}

public sealed class AgentSession(string name, DateTimeOffset openedOn)
{
    private readonly Channel<LocalEvent> _events = Channel.CreateUnbounded<LocalEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _ended = new();

    public string Name { get; } = name;

    public DateTimeOffset LastHeartbeat { get; private set; } = openedOn;

    /// <summary>True when the agent unregistered cleanly (it shows as left, not timed out).</summary>
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
