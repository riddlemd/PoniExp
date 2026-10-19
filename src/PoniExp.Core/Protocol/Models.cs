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

/// <summary>
/// Asks to join, signed by the key it asks to admit (proof that the sender holds that key). Carries the
/// machine's encryption key, with the identity key's signature over it (required).
/// </summary>
public sealed record JoinRequest(
    string Name,
    MachineDescription Description,
    string PublicKey,
    string EncryptionKey,
    string EncryptionKeySignature,
    DateTimeOffset RequestedOn,
    string Signature);

/// <summary>
/// The part of a message only the two endpoints need. It travels encrypted (see <see cref="Envelope.Ciphertext"/>):
/// the router never sees it.
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
/// A message between machines: the header the router needs for routing and expiry, and the content,
/// encrypted. <see cref="FromMachine"/> and <see cref="ToMachine"/> are machine ids; exactly one recipient.
/// <see cref="Size"/> is the ciphertext's length in bytes. The sender signs all of it.
/// </summary>
public sealed record Envelope(
    Guid Id,
    string FromMachine,
    string ToMachine,
    DateTimeOffset ExpiresOn,
    long Sequence,
    int Size,
    string Ciphertext,
    IReadOnlyList<KeyWrap> Wraps,
    string Signature);

/// <summary>
/// The message's content key, wrapped for one reader (the recipient, or the sender's own machine) under a
/// fresh ephemeral ECDH key. Base64 throughout.
/// </summary>
public sealed record KeyWrap(string Machine, string EphemeralKey, string WrappedKey);

/// <summary>An envelope as the router hands it over, with when the router received it (the router's word, unsigned).</summary>
public sealed record Delivery(Envelope Envelope, DateTimeOffset RelayedOn);

public sealed record HelloResult(int ProtocolVersion);

public sealed record AuthResult(MembershipStatus Status, string MachineId, string? Name);

public sealed record SendResult(Guid Id, DeliveryState State);

/// <summary>
/// An approved machine, as the router describes it. Check <see cref="EncryptionKey"/> against
/// <see cref="EncryptionKeySignature"/> and <see cref="PublicKey"/> before encrypting to it: the router could lie.
/// </summary>
public sealed record MachineInfo(
    string Id,
    string Name,
    MachineDescription Description,
    string PublicKey,
    string EncryptionKey,
    string EncryptionKeySignature,
    bool Online,
    DateTimeOffset? LastSeenOn);

public sealed record PresenceChange(string MachineId, string Name, bool Online, DateTimeOffset OccurredOn);
