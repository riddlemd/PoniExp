using System.Runtime.InteropServices;

namespace PoniExp.Core.Protocol;

public enum MembershipStatus
{
    /// <summary>The router has never seen this key.</summary>
    Unknown,
    Pending,
    Approved,
    Rejected,
}

public enum DeliveryState
{
    /// <summary>Pushed to at least one live connection of the recipient (kept until it acknowledges).</summary>
    Delivered,
    /// <summary>The recipient is offline; it gets the envelope when it next connects.</summary>
    Queued,
}

public sealed record MachineDescription(string OperatingSystem, string Architecture, string HostName, string? Notes = null)
{
    public static MachineDescription Detect(string? notes = null) =>
        new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Environment.MachineName, notes);
}

/// <summary>Asks to join, signed by the key it asks to admit (proof that the sender holds that key).</summary>
public sealed record JoinRequest(string Name, MachineDescription Description, string PublicKey, DateTimeOffset RequestedAt, string Signature);

/// <summary>A message between machines. <see cref="From"/> and <see cref="To"/> are machine ids.</summary>
public sealed record Envelope(Guid Id, string From, string To, string Kind, string Body, DateTimeOffset CreatedAt, string Signature);

public sealed record AuthResult(MembershipStatus Status, string MachineId, string? Name);

public sealed record SendResult(Guid Id, DeliveryState State);

public sealed record MachineInfo(string Id, string Name, MachineDescription Description, string PublicKey, bool Online, DateTimeOffset? LastSeen);

public sealed record PresenceChange(string MachineId, string Name, bool Online, DateTimeOffset At);
