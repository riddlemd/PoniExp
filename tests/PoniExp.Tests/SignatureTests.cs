using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

public class SignatureTests
{
    private static readonly MachineDescription Description = new("Linux", "x64", "box");

    [Fact]
    public void JoinRequestVerifiesOnlyUnchanged()
    {
        using var keys = MachineKeys.Create();
        using var other = MachineKeys.Create();
        var request = Signatures.SignJoin(keys, "ise", Description, DateTimeOffset.UtcNow);

        Assert.True(Signatures.VerifyJoin(request));
        Assert.False(Signatures.VerifyJoin(request with { Name = "wojtek" }));
        Assert.False(Signatures.VerifyJoin(request with { Description = Description with { Notes = "x" } }));
        Assert.False(Signatures.VerifyJoin(request with { RequestedOn = request.RequestedOn.AddSeconds(1) }));
        Assert.False(Signatures.VerifyJoin(request with { EncryptionKey = other.EncryptionPublicKey }));
        Assert.False(Signatures.VerifyJoin(request with { PublicKey = other.PublicKey }));
    }

    [Fact]
    public void TheSignatureCoversEveryFieldAndOnlyItsSender()
    {
        using var sender = MachineKeys.Create();
        using var other = MachineKeys.Create();
        var now = DateTimeOffset.UtcNow;
        var envelope = Signatures.SealEnvelope(sender, Guid.NewGuid(), other.MachineId, other.EncryptionPublicKey, 7, now.AddHours(1),
            Envelopes.Content(now, "hello"));
        var wrap = envelope.Wraps[0];

        Assert.True(Signatures.VerifyEnvelope(envelope, sender.PublicKey));
        Assert.False(Signatures.VerifyEnvelope(envelope, other.PublicKey));
        Envelope[] tampered =
        [
            envelope with { Id = Guid.NewGuid() },
            envelope with { FromMachine = other.MachineId },
            envelope with { ToMachine = sender.MachineId },
            envelope with { ExpiresOn = envelope.ExpiresOn.AddSeconds(1) },
            envelope with { Sequence = 8 },
            envelope with { Size = envelope.Size + 1 },
            envelope with { Ciphertext = RelayTests.Flip(envelope.Ciphertext) },
            envelope with { Wraps = [wrap with { Machine = sender.MachineId }, envelope.Wraps[1]] },
            envelope with { Wraps = [wrap with { EphemeralKey = envelope.Wraps[1].EphemeralKey }, envelope.Wraps[1]] },
            envelope with { Wraps = [wrap with { WrappedKey = RelayTests.Flip(wrap.WrappedKey) }, envelope.Wraps[1]] },
            envelope with { Wraps = [wrap] },
        ];
        Assert.All(tampered, t => Assert.False(Signatures.VerifyEnvelope(t, sender.PublicKey)));
    }

    [Fact]
    public void FieldsCantBleedIntoEachOther()
    {
        Assert.NotEqual(SigningPayload.Build("x", "a\n1:b", ""), SigningPayload.Build("x", "a", "b"));
        Assert.NotEqual(SigningPayload.Build("x", "ab", "c"), SigningPayload.Build("x", "a", "bc"));
    }

    [Fact]
    public void ASignatureForOnePurposeIsNoGoodForAnother()
    {
        using var identity = MachineIdentity.Create();
        var signature = identity.Sign(SigningPayload.Build("auth", "a", "b"));

        Assert.False(PublicKeys.Verify(identity.PublicKey, SigningPayload.Build("join", "a", "b"), signature));
    }

    [Fact]
    public void KeysRoundTripThroughPemAndKeepTheirId()
    {
        using var identity = MachineIdentity.Create();
        using var loaded = MachineIdentity.FromPem(identity.ExportPrivateKeyPem());

        Assert.Equal(identity.MachineId, loaded.MachineId);
        Assert.Equal(32, identity.MachineId.Length);
        Assert.Matches("^([0-9a-f]{4}-){5}[0-9a-f]{4}$", KeyFingerprint.JoinCodeOf(identity.PublicKey));
    }

    [Fact]
    public void GarbageKeysAreInvalidNotExceptions()
    {
        Assert.False(PublicKeys.IsValid("not base64!"));
        Assert.False(PublicKeys.IsValid(Convert.ToBase64String([1, 2, 3])));
        Assert.False(PublicKeys.Verify("AAAA", [1], "AAAA"));
    }

    [Fact]
    public void OtherCurvesAreRefused()
    {
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);

        Assert.False(PublicKeys.IsValid(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => MachineIdentity.FromPem(p384.ExportPkcs8PrivateKeyPem()));
    }
}
