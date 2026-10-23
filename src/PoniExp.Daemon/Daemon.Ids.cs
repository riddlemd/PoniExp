namespace PoniExp.Daemon;

/// <summary>
/// Ids as agents and people type them: the whole id, or a unique prefix of at least <see cref="MinIdPrefix"/>
/// characters (the rule approve and deny use). Resolved here, once, so the CLI and MCP tools get it alike.
/// </summary>
public sealed partial class Daemon
{
    /// <summary>A shorter prefix matches by accident too often to be taken.</summary>
    internal const int MinIdPrefix = 4;

    /// <summary>One of <paramref name="agent"/>'s messages: received, and with <paramref name="sent"/> also sent.</summary>
    internal Guid ResolveMessageId(string agent, string id, bool sent = false) =>
        ResolveId(id, "messages", prefix => Store.FindMessageIds(MachineId, agent, prefix, sent), $"no message {id} for agent '{agent}'");

    /// <summary>One of <paramref name="agent"/>'s conversations.</summary>
    internal Guid ResolveConversationId(string agent, string id) =>
        ResolveId(id, "conversations", prefix => Store.FindConversationIds(MachineId, agent, prefix), $"agent '{agent}' has no conversation {id}");

    /// <summary>A whole id is taken as it is (what it names is checked where it's used); a prefix must match exactly one.</summary>
    private static Guid ResolveId(string id, string what, Func<string, IReadOnlyList<Guid>> find, string notFound)
    {
        if (Guid.TryParse(id, out var exact))
        {
            return exact;
        }

        var matches = id.Length >= MinIdPrefix ? find(id) : [];
        return matches switch
        {
            [var one] => one,
            [] => throw new KeyNotFoundException(notFound),
            _ => throw new ArgumentException($"'{id}' matches {matches.Count} {what}; use more of its id"),
        };
    }
}
