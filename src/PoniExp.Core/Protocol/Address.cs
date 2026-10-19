namespace PoniExp.Core.Protocol;

/// <summary>An agent on a machine: <c>machine/endpoint</c>. The machine part may be a name or an id until resolved.</summary>
public readonly record struct Address(string Machine, string Endpoint)
{
    public const string DefaultEndpoint = "default";

    /// <summary>The daemon's own endpoint, for automatic messages between daemons (announcements). Agents can't register it.</summary>
    public const string DaemonEndpoint = "daemon";

    /// <summary>Parses <c>machine</c> or <c>machine/endpoint</c>; a bare machine means its default endpoint.</summary>
    public static Address Parse(string text)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var address = slash < 0 ? new Address(text, DefaultEndpoint) : new Address(text[..slash], text[(slash + 1)..]);
        if (address.Machine.Length == 0 || !Limits.IsValidEndpoint(address.Endpoint))
        {
            throw new FormatException($"'{text}' isn't machine or machine/endpoint (endpoint: {Limits.EndpointRule}).");
        }

        return address;
    }

    public override string ToString() => $"{Machine}/{Endpoint}";

    public const string RolePrefix = "role:";
    public const string GroupPrefix = "group:";
    public const string AllSuffix = "/all";

    /// <summary>
    /// <c>role:name</c> (any one holder), <c>role:name/all</c> (every holder) or <c>group:name</c>; null for a plain address.
    /// Role and group names follow the endpoint rules.
    /// </summary>
    public static Indirect? ParseIndirect(string text)
    {
        if (text.StartsWith(RolePrefix, StringComparison.Ordinal))
        {
            var rest = text[RolePrefix.Length..];
            var all = rest.EndsWith(AllSuffix, StringComparison.Ordinal);
            var role = all ? rest[..^AllSuffix.Length] : rest;
            return Limits.IsValidEndpoint(role) ? new Indirect(IndirectKind.Role, role, all)
                : throw new FormatException($"'{text}': a role is {Limits.EndpointRule}");
        }

        if (text.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            var group = text[GroupPrefix.Length..];
            return Limits.IsValidEndpoint(group) ? new Indirect(IndirectKind.Group, group, true)
                : throw new FormatException($"'{text}': a group is {Limits.EndpointRule}");
        }

        return null;
    }
}

public enum IndirectKind
{
    Role,
    Group,
}

/// <summary>A role or group address, resolved by the sender's daemon into plain ones.</summary>
public readonly record struct Indirect(IndirectKind Kind, string Name, bool All)
{
    public override string ToString() => Kind == IndirectKind.Role ? $"{Address.RolePrefix}{Name}{(All ? Address.AllSuffix : "")}" : $"{Address.GroupPrefix}{Name}";
}
