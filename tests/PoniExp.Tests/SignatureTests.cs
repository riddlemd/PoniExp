using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

public class SignatureTests
{
    private static readonly MachineDescription Description = new("Linux", "x64", "box");

    [Fact]
    public void JoinRequestVerifiesOnlyUnchanged()
    {
        using var identity = MachineIdentity.Create();
        var request = Signatures.SignJoin(identity, "ise", Description, DateTimeOffset.UtcNow);

        Assert.True(Signatures.VerifyJoin(request));
        Assert.False(Signatures.VerifyJoin(request with { Name = "wojtek" }));
        Assert.False(Signatures.VerifyJoin(request with { Description = Description with { Notes = "x" } }));
        Assert.False(Signatures.VerifyJoin(request with { RequestedAt = request.RequestedAt.AddSeconds(1) }));
    }

    [Fact]
    public void JoinRequestWithSomeoneElsesKeyFails()
    {
        using var mine = MachineIdentity.Create();
        using var theirs = MachineIdentity.Create();
        var request = Signatures.SignJoin(mine, "ise", Description, DateTimeOffset.UtcNow);

        Assert.False(Signatures.VerifyJoin(request with { PublicKey = theirs.PublicKey }));
    }

    [Fact]
    public void EnvelopeVerifiesOnlyUnchangedAndOnlyForItsSender()
    {
        using var sender = MachineIdentity.Create();
        using var other = MachineIdentity.Create();
        var envelope = Signatures.SignEnvelope(sender, other.MachineId, "chat", "hello", DateTimeOffset.UtcNow);

        Assert.True(Signatures.VerifyEnvelope(envelope, sender.PublicKey));
        Assert.False(Signatures.VerifyEnvelope(envelope with { Body = "hellO" }, sender.PublicKey));
        Assert.False(Signatures.VerifyEnvelope(envelope with { To = sender.MachineId }, sender.PublicKey));
        Assert.False(Signatures.VerifyEnvelope(envelope with { Kind = "task" }, sender.PublicKey));
        Assert.False(Signatures.VerifyEnvelope(envelope, other.PublicKey));
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
