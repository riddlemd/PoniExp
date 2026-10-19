using System.Globalization;
using PoniExp.Core.Crypto;

namespace PoniExp.Core.Protocol;

/// <summary>What each protocol message signs, and how to make and check those signatures.</summary>
public static class Signatures
{
    public static byte[] JoinPayload(JoinRequest request) =>
        SigningPayload.Build("join", request.Name, request.Description.OperatingSystem, request.Description.Architecture,
            request.Description.HostName, request.Description.Notes, request.PublicKey, request.EncryptionKey,
            request.EncryptionKeySignature, Stamp(request.RequestedOn));

    /// <summary>The answer to a router challenge: binds this connection to the key.</summary>
    public static byte[] AuthPayload(string challenge, string publicKey) =>
        SigningPayload.Build("auth", challenge, publicKey);

    /// <summary>What a machine's identity key signs to vouch for its encryption key.</summary>
    public static byte[] EncryptionKeyPayload(string identityPublicKey, string encryptionPublicKey) =>
        SigningPayload.Build("encryption-key", identityPublicKey, encryptionPublicKey);

    /// <summary>Every header field, the ciphertext and every key wrap: the signature covers the whole message.</summary>
    public static byte[] EnvelopePayload(Envelope envelope) =>
        SigningPayload.Build("envelope", [
            envelope.Id.ToString("D"),
            envelope.FromMachine,
            envelope.ToMachine,
            Stamp(envelope.ExpiresOn),
            envelope.Sequence.ToString(CultureInfo.InvariantCulture),
            envelope.Size.ToString(CultureInfo.InvariantCulture),
            envelope.Ciphertext,
            .. Wraps(envelope.Wraps),
        ]);

    public static JoinRequest SignJoin(MachineKeys keys, string name, MachineDescription description, DateTimeOffset now)
    {
        var unsigned = new JoinRequest(name, description, keys.PublicKey, keys.EncryptionPublicKey, keys.EncryptionKeySignature,
            Truncate(now), string.Empty);
        return unsigned with { Signature = keys.Sign(JoinPayload(unsigned)) };
    }

    /// <summary>
    /// Encrypts <paramref name="content"/> for the recipient and for the sender's own machine, and signs the
    /// envelope. <paramref name="recipientEncryptionKey"/> must already be verified (see
    /// <see cref="PublicKeys.VerifyEncryptionKey"/>). Timestamps are truncated to milliseconds first.
    /// </summary>
    public static Envelope SealEnvelope(MachineKeys sender, Guid id, string toMachine, string recipientEncryptionKey, long sequence,
        DateTimeOffset expiresOn, MessageContent content)
    {
        var normalisedExpiry = Truncate(expiresOn);
        var normalised = content with { CreatedOn = Truncate(content.CreatedOn) };
        var sealedContent = MessageCrypto.Seal(normalised, id, sender.MachineId, toMachine, sequence, normalisedExpiry,
            [new(toMachine, recipientEncryptionKey), new(sender.MachineId, sender.EncryptionPublicKey)]);
        var unsigned = new Envelope(id, sender.MachineId, toMachine, normalisedExpiry, sequence, sealedContent.Size,
            sealedContent.Ciphertext, sealedContent.Wraps, string.Empty);
        return unsigned with { Signature = sender.Sign(EnvelopePayload(unsigned)) };
    }

    /// <summary>
    /// Seals a room message once: addressed to <c>room:&lt;id&gt;</c>, its content key wrapped for every reader (each
    /// member machine's verified encryption key) and the sender. The router fans it out to the machines it's wrapped for.
    /// </summary>
    public static Envelope SealRoomEnvelope(MachineKeys sender, Guid id, Guid roomId, IEnumerable<MessageCrypto.Reader> readers, long sequence,
        DateTimeOffset expiresOn, MessageContent content)
    {
        var normalisedExpiry = Truncate(expiresOn);
        var normalised = content with { CreatedOn = Truncate(content.CreatedOn) };
        var to = RoomAddress.Of(roomId);
        var sealedContent = MessageCrypto.Seal(normalised, id, sender.MachineId, to, sequence, normalisedExpiry,
            [.. readers, new(sender.MachineId, sender.EncryptionPublicKey)]);
        var unsigned = new Envelope(id, sender.MachineId, to, normalisedExpiry, sequence, sealedContent.Size, sealedContent.Ciphertext, sealedContent.Wraps,
            string.Empty);
        return unsigned with { Signature = sender.Sign(EnvelopePayload(unsigned)) };
    }

    public static bool VerifyJoin(JoinRequest request) =>
        PublicKeys.Verify(request.PublicKey, JoinPayload(request), request.Signature)
        && PublicKeys.VerifyEncryptionKey(request.PublicKey, request.EncryptionKey, request.EncryptionKeySignature);

    /// <summary>Signed by <paramref name="senderPublicKey"/>, whose hash is the sender id, with a consistent size.</summary>
    public static bool VerifyEnvelope(Envelope envelope, string senderPublicKey)
    {
        if (envelope.Ciphertext is null || envelope.Wraps is null || KeyFingerprint.MachineIdOf(senderPublicKey) != envelope.FromMachine)
        {
            return false;
        }

        try
        {
            return envelope.Size == Convert.FromBase64String(envelope.Ciphertext).Length
                   && PublicKeys.Verify(senderPublicKey, EnvelopePayload(envelope), envelope.Signature);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Milliseconds survive every JSON round trip; ticks beyond them might not.
    public static DateTimeOffset Truncate(DateTimeOffset time) =>
        DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());

    public static string Stamp(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static IEnumerable<string?> Wraps(IReadOnlyList<KeyWrap>? wraps)
    {
        wraps ??= [];
        yield return wraps.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var wrap in wraps)
        {
            yield return wrap.Machine;
            yield return wrap.EphemeralKey;
            yield return wrap.WrappedKey;
        }
    }
}
