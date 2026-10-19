using System.Globalization;
using PoniExp.Core.Crypto;

namespace PoniExp.Core.Protocol;

/// <summary>What each protocol message signs, and how to make and check those signatures.</summary>
public static class Signatures
{
    public static byte[] JoinPayload(JoinRequest request) =>
        SigningPayload.Build("join", request.Name, request.Description.OperatingSystem, request.Description.Architecture,
            request.Description.HostName, request.Description.Notes, request.PublicKey, Stamp(request.RequestedAt));

    /// <summary>The answer to a router challenge: binds this connection to the key.</summary>
    public static byte[] AuthPayload(string challenge, string publicKey) =>
        SigningPayload.Build("auth", challenge, publicKey);

    public static byte[] EnvelopePayload(Envelope envelope) =>
        SigningPayload.Build("envelope", envelope.Id.ToString("D"), envelope.From, envelope.To, envelope.Kind, envelope.Body,
            Stamp(envelope.CreatedAt));

    public static JoinRequest SignJoin(MachineIdentity identity, string name, MachineDescription description, DateTimeOffset now)
    {
        var unsigned = new JoinRequest(name, description, identity.PublicKey, Truncate(now), string.Empty);
        return unsigned with { Signature = identity.Sign(JoinPayload(unsigned)) };
    }

    public static Envelope SignEnvelope(MachineIdentity identity, string to, string kind, string body, DateTimeOffset now)
    {
        var unsigned = new Envelope(Guid.NewGuid(), identity.MachineId, to, kind, body, Truncate(now), string.Empty);
        return unsigned with { Signature = identity.Sign(EnvelopePayload(unsigned)) };
    }

    public static bool VerifyJoin(JoinRequest request) =>
        PublicKeys.Verify(request.PublicKey, JoinPayload(request), request.Signature);

    public static bool VerifyEnvelope(Envelope envelope, string senderPublicKey) =>
        KeyFingerprint.MachineIdOf(senderPublicKey) == envelope.From
        && PublicKeys.Verify(senderPublicKey, EnvelopePayload(envelope), envelope.Signature);

    // Milliseconds survive every JSON round trip; ticks beyond them might not.
    private static DateTimeOffset Truncate(DateTimeOffset time) =>
        DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());

    private static string Stamp(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
