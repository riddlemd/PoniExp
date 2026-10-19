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
    /// <summary>Pushed to at least one live connection of the recipient (kept until it confirms).</summary>
    Pushed,
    /// <summary>The recipient is offline; it gets the envelope when it next connects.</summary>
    Queued,
}

/// <summary>
/// What a message is, at the protocol level. A closed list that only PoniExp defines, because it decides
/// how the daemon handles a message. Agents' own protocols ride on <see cref="Message"/>, defined by its
/// content type, metadata and body. There is no reply type: a reply is a message with ReplyTo set.
/// </summary>
public enum MessageType
{
    Message,
    Receipt,
    FileReceipt,
    Endpoint,
}

/// <summary>The outcome of the work a message asked for, carried on replies. Anything but Failed is the success path.</summary>
public enum ReplyStatus
{
    Progress,
    Done,
    Failed,
}

public sealed record MachineDescription(string OperatingSystem, string Architecture, string HostName, string? Notes = null)
{
    public static MachineDescription Detect(string? notes = null) =>
        new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Environment.MachineName, notes);
}

/// <summary>Asks to join, signed by the key it asks to admit (proof that the sender holds that key).</summary>
public sealed record JoinRequest(string Name, MachineDescription Description, string PublicKey, DateTimeOffset RequestedOn, string Signature);

/// <summary>
/// The part of a message only the two endpoints need. Signed (with the header) by the sender; from phase 2
/// it is also encrypted, so the router never sees it.
/// </summary>
public sealed record MessageContent(
    string FromEndpoint,
    string ToEndpoint,
    Guid ConversationId,
    Guid? ReplyTo,
    Guid OperationId,
    ReplyStatus? Status,
    string? Subject,
    MessageType Type,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    string Body,
    DateTimeOffset CreatedOn);

/// <summary>
/// A message between machines: the header the router needs for routing and expiry, plus the content.
/// <see cref="FromMachine"/> and <see cref="ToMachine"/> are machine ids. Exactly one recipient.
/// </summary>
public sealed record Envelope(
    Guid Id,
    string FromMachine,
    string ToMachine,
    DateTimeOffset ExpiresOn,
    long Sequence,
    int Size,
    MessageContent Content,
    string Signature);

/// <summary>An envelope as the router hands it over, with when the router received it (the router's word, unsigned).</summary>
public sealed record Delivery(Envelope Envelope, DateTimeOffset RelayedOn);

public sealed record HelloResult(int ProtocolVersion);

public sealed record AuthResult(MembershipStatus Status, string MachineId, string? Name);

public sealed record SendResult(Guid Id, DeliveryState State);

public sealed record MachineInfo(string Id, string Name, MachineDescription Description, string PublicKey, bool Online, DateTimeOffset? LastSeenOn);

public sealed record PresenceChange(string MachineId, string Name, bool Online, DateTimeOffset OccurredOn);
