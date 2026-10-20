namespace PoniExp.Core.Protocol;

/// <summary>An agent on a machine: <c>machine/agent</c>. The machine part may be a name or an id until resolved.</summary>
public readonly record struct Address(string Machine, string Agent)
{
    public const string DefaultAgent = "default";

    /// <summary>The daemon's own agent, for automatic messages between daemons (announcements). Agents can't register it.</summary>
    public const string DaemonAgent = "daemon";

    /// <summary>Parses <c>machine</c> or <c>machine/agent</c>; a bare machine means its default agent.</summary>
    public static Address Parse(string text)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var address = slash < 0 ? new Address(text, DefaultAgent) : new Address(text[..slash], text[(slash + 1)..]);
        if (address.Machine.Length == 0 || !Limits.IsValidAgent(address.Agent))
        {
            throw new FormatException($"'{text}' isn't machine or machine/agent (agent: {Limits.AgentRule}).");
        }

        return address;
    }

    public override string ToString() => $"{Machine}/{Agent}";

    public const string GroupPrefix = "group:";

    /// <summary><c>group:name</c> (the owner's list); null for a plain address. Group names follow the agent rules.</summary>
    public static Indirect? ParseIndirect(string text)
    {
        if (text.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            var group = text[GroupPrefix.Length..];
            return Limits.IsValidAgent(group) ? new Indirect(group) : throw new FormatException($"'{text}': a group is {Limits.AgentRule}");
        }

        return null;
    }
}

/// <summary>A group address, resolved by the sender's daemon into plain ones.</summary>
public readonly record struct Indirect(string Name)
{
    public override string ToString() => $"{Address.GroupPrefix}{Name}";
}
