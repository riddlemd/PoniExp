using System.Globalization;
using PoniExp.Core.Crypto;

namespace PoniExp.Core.Protocol;

/// <summary>What each protocol message signs, and how to make and check those signatures.</summary>
public static class Signatures
{
    public static byte[] JoinPayload(JoinRequest request) =>
        SigningPayload.Build("join", request.Name, request.Description.OperatingSystem, request.Description.Architecture,
            request.Description.HostName, request.Description.Notes, request.PublicKey, Stamp(request.RequestedOn));

    /// <summary>The answer to a router challenge: binds this connection to the key.</summary>
    public static byte[] AuthPayload(string challenge, string publicKey) =>
        SigningPayload.Build("auth", challenge, publicKey);

    /// <summary>
    /// The content in its signed form. Its length is the envelope's Size; in phase 2 these bytes are what
    /// gets encrypted.
    /// </summary>
    public static byte[] ContentPayload(MessageContent content) =>
        SigningPayload.Build("content", [
            content.FromEndpoint,
            content.ToEndpoint,
            content.ConversationId.ToString("D"),
            content.ReplyTo?.ToString("D"),
            content.OperationId.ToString("D"),
            content.Status is { } status ? WireProtocol.NameOf(status) : null,
            content.Subject,
            WireProtocol.NameOf(content.Type),
            content.ContentType,
            .. Metadata(content.Metadata),
            content.Body,
            Stamp(content.CreatedOn),
        ]);

    /// <summary>Every header field, and the content: the signature covers the whole message.</summary>
    public static byte[] EnvelopePayload(Envelope envelope) =>
        SigningPayload.Build("envelope", [
            envelope.Id.ToString("D"),
            envelope.FromMachine,
            envelope.ToMachine,
            Stamp(envelope.ExpiresOn),
            envelope.Sequence.ToString(CultureInfo.InvariantCulture),
            envelope.Size.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(ContentPayload(envelope.Content)),
        ]);

    public static int SizeOf(MessageContent content) => ContentPayload(content).Length;

    public static JoinRequest SignJoin(MachineIdentity identity, string name, MachineDescription description, DateTimeOffset now)
    {
        var unsigned = new JoinRequest(name, description, identity.PublicKey, Truncate(now), string.Empty);
        return unsigned with { Signature = identity.Sign(JoinPayload(unsigned)) };
    }

    /// <summary>Signs a message to <paramref name="toMachine"/>. Timestamps are truncated to milliseconds first.</summary>
    public static Envelope SignEnvelope(MachineIdentity identity, Guid id, string toMachine, long sequence, DateTimeOffset expiresOn,
        MessageContent content)
    {
        var normalised = content with { CreatedOn = Truncate(content.CreatedOn) };
        var unsigned = new Envelope(id, identity.MachineId, toMachine, Truncate(expiresOn), sequence, SizeOf(normalised), normalised,
            string.Empty);
        return unsigned with { Signature = identity.Sign(EnvelopePayload(unsigned)) };
    }

    public static bool VerifyJoin(JoinRequest request) =>
        PublicKeys.Verify(request.PublicKey, JoinPayload(request), request.Signature);

    public static bool VerifyEnvelope(Envelope envelope, string senderPublicKey) =>
        envelope.Content is not null
        && KeyFingerprint.MachineIdOf(senderPublicKey) == envelope.FromMachine
        && envelope.Size == SizeOf(envelope.Content)
        && PublicKeys.Verify(senderPublicKey, EnvelopePayload(envelope), envelope.Signature);

    // Milliseconds survive every JSON round trip; ticks beyond them might not.
    public static DateTimeOffset Truncate(DateTimeOffset time) =>
        DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());

    public static string Stamp(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // A count, then sorted key/value pairs: the same map always signs the same way, whatever its order.
    private static IEnumerable<string?> Metadata(IReadOnlyDictionary<string, string>? metadata)
    {
        var entries = (metadata ?? new Dictionary<string, string>()).OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
        yield return entries.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var (key, value) in entries)
        {
            yield return key;
            yield return value;
        }
    }
}
