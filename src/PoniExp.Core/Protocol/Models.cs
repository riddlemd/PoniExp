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
    Agent,
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
/// The part of a message only the two agents need. It travels encrypted (see <see cref="Envelope.Ciphertext"/>):
/// the router never sees it.
/// </summary>
public sealed record MessageContent(
    string FromAgent,
    string ToAgent,
    Guid ConversationId,
    Guid? ReplyTo,
    Guid OperationId,
    ReplyStatus? Status,
    string? Subject,
    MessageType Type,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    string Body,
    DateTimeOffset CreatedOn,
    IReadOnlyList<Attachment>? Attachments = null);

/// <summary>
/// A file sent with a message: a blob on the router (see <see cref="Files.BlobCrypto"/>). It travels inside the
/// encrypted, signed content, so only the recipient (and the sender) learn the file key, and the router can't
/// swap the blob: <see cref="ChunksHash"/> pins its contents. <see cref="Key"/> is base64; <see cref="ChunksHash"/>
/// is the lowercase hex SHA-256 over the chunk hashes.
/// </summary>
public sealed record Attachment(Guid BlobId, string Name, string ContentType, long Size, int ChunkCount, string Key, string ChunksHash);

/// <summary>Which file receipt (see <see cref="FileReceiptBody"/>).</summary>
public enum FileReceiptKind
{
    /// <summary>The blob was fetched and verified on the recipient's machine.</summary>
    Downloaded,
    /// <summary>It couldn't be: tampered with, expired, cancelled by the sender, or over a limit (the reason says).</summary>
    DownloadFailed,
}

/// <summary>
/// The body of a <see cref="MessageType.FileReceipt"/> message (JSON), one per message that carried the file.
/// Its ReplyTo is that message. Separate from delivery receipts and from a reply's status.
/// </summary>
public sealed record FileReceiptBody(FileReceiptKind Receipt, Guid BlobId, string? Reason = null);

/// <summary>
/// Starts an upload: the machines the blob is shared with (fixed from here on), its encrypted size (what is
/// reserved against the limits) and its chunk count.
/// </summary>
public sealed record BeginUploadRequest(IReadOnlyList<string> ShareWith, long EncryptedBytes, int ChunkCount);

/// <summary>A short-lived ticket for one blob in one direction (upload or download), for the <c>/blobs</c> HTTP calls.</summary>
public sealed record BlobTicket(Guid BlobId, string Ticket, DateTimeOffset ExpiresOn);

/// <summary>What of a blob is on the router: for resuming an upload, or before a download. Never lists who it's shared with.</summary>
public sealed record BlobStatus(Guid BlobId, BlobState State, int ChunkCount, IReadOnlyList<int> Chunks, bool Manifest, long Bytes);

public enum BlobState
{
    Uploading,
    Sealed,
    Cancelled,
    Expired,
}

/// <summary>A blob as its sender sees it: who it's shared with and which of them have released it (machine names).</summary>
public sealed record SenderBlob(Guid BlobId, BlobState State, long Bytes, IReadOnlyList<string> SharedWith, IReadOnlyList<string> ReleasedBy,
    DateTimeOffset CreatedOn, DateTimeOffset ExpiresOn);

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

/// <summary>
/// The router's answer to Hello: its protocol version, its clock and how far it lets clocks differ, so a
/// client can tell when its own clock is off (<c>poniexp-client doctor</c>).
/// </summary>
public sealed record HelloResult(int ProtocolVersion, DateTimeOffset ServerTime, TimeSpan ClockSkew);

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

/// <summary>Which delivery receipt (see <see cref="ReceiptBody"/>).</summary>
public enum ReceiptKind
{
    /// <summary>The recipient's machine has stored the message, whether or not the agent is online.</summary>
    Received,
    /// <summary>An agent has taken the message on (an explicit ack, or a reply).</summary>
    Acknowledged,
    /// <summary>The recipient's machine won't deliver it (e.g. unknown agent). Sent instead of Received.</summary>
    Rejected,
}

/// <summary>
/// The body of a <see cref="MessageType.Receipt"/> message (JSON). The receipt's ReplyTo is the message it
/// is about. Signed and encrypted like any message, so the router can't fake one.
/// </summary>
public sealed record ReceiptBody(ReceiptKind Receipt, string? Reason = null);

/// <summary>An agent's state as its daemon announces it.</summary>
public enum AgentState
{
    Online,
    /// <summary>Unregistered cleanly.</summary>
    Left,
    /// <summary>Stopped without unregistering, or missed its heartbeat.</summary>
    TimedOut,
}

/// <summary>An agent as its daemon announces it, with the roles it declared (see docs/plan.md, Roles).</summary>
public sealed record AnnouncedAgent(string Name, AgentState State, DateTimeOffset ChangedOn, IReadOnlyList<string>? Roles = null);

/// <summary>
/// The body of a <see cref="MessageType.Agent"/> message (JSON): the announcing daemon's complete list of
/// agents (retired ones are left out). <see cref="Counter"/> only ever increases per daemon, so a
/// receiver keeps the newest list and ignores a replayed or late one.
/// </summary>
public sealed record AgentAnnouncement(long Counter, IReadOnlyList<AnnouncedAgent> Agents);

/// <summary>An owner-defined group: a named list of addresses (<c>machine/agent</c> or <c>role:...</c>), by machine name.</summary>
public sealed record GroupInfo(string Name, IReadOnlyList<string> Members);

/// <summary>How a room envelope is addressed: <c>room:&lt;id&gt;</c> in <see cref="Envelope.ToMachine"/>, signed like every header field.</summary>
public static class RoomAddress
{
    public const string Prefix = "room:";

    /// <summary>The agent a room message's content names: the room as a whole.</summary>
    public const string Agent = "room";

    public static string Of(Guid roomId) => Prefix + roomId.ToString("D");

    public static bool TryParse(string? toMachine, out Guid roomId)
    {
        roomId = Guid.Empty;
        return toMachine is not null && toMachine.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParse(toMachine[Prefix.Length..], out roomId)
               && roomId != Guid.Empty;
    }
}
