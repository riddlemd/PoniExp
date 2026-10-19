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
}
