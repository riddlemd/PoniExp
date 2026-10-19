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
        Assert.False(Signatures.VerifyJoin(request with { RequestedOn = request.RequestedOn.AddSeconds(1) }));
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
    public void TheSignatureCoversEveryFieldAndOnlyItsSender()
    {
        using var sender = MachineIdentity.Create();
        using var other = MachineIdentity.Create();
        var now = DateTimeOffset.UtcNow;
        var content = Envelopes.Content(now, "hello") with
        {
            ReplyTo = Guid.NewGuid(),
            Status = ReplyStatus.Progress,
            Subject = "subject",
            Metadata = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
        };
        var envelope = Signatures.SignEnvelope(sender, Guid.NewGuid(), other.MachineId, 7, now.AddHours(1), content);
        var c = envelope.Content;

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
            envelope with { Content = c with { FromEndpoint = "other" } },
            envelope with { Content = c with { ToEndpoint = "other" } },
            envelope with { Content = c with { ConversationId = Guid.NewGuid() } },
            envelope with { Content = c with { ReplyTo = Guid.NewGuid() } },
            envelope with { Content = c with { OperationId = Guid.NewGuid() } },
            envelope with { Content = c with { Status = ReplyStatus.Done } },
            envelope with { Content = c with { Subject = "Subject" } },
            envelope with { Content = c with { Type = MessageType.Receipt } },
            envelope with { Content = c with { ContentType = "text/markdown" } },
            envelope with { Content = c with { Metadata = new Dictionary<string, string> { ["a"] = "1", ["b"] = "3" } } },
            envelope with { Content = c with { Metadata = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "" } } },
            envelope with { Content = c with { Body = "hellO" } },
            envelope with { Content = c with { CreatedOn = c.CreatedOn.AddMilliseconds(1) } },
        ];
        Assert.All(tampered, t => Assert.False(Signatures.VerifyEnvelope(t, sender.PublicKey)));

        // The same metadata in another order is the same message.
        var reordered = envelope with { Content = c with { Metadata = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" } } };
        Assert.True(Signatures.VerifyEnvelope(reordered, sender.PublicKey));
    }

    [Theory]
    [InlineData("wojtek", "wojtek", "default")]
    [InlineData("wojtek/agent", "wojtek", "agent")]
    [InlineData("3f2a91c0/hermes.main", "3f2a91c0", "hermes.main")]
    public void AddressesParse(string text, string machine, string endpoint) =>
        Assert.Equal(new Address(machine, endpoint), Address.Parse(text));

    [Theory]
    [InlineData("wojtek/")]
    [InlineData("/agent")]
    [InlineData("wojtek/Agent")]
    [InlineData("wojtek/a/b")]
    public void BadAddressesDont(string text) => Assert.Throws<FormatException>(() => Address.Parse(text));

    [Fact]
    public void MessageRulesCatchMalformedContent()
    {
        var good = Envelopes.Content(DateTimeOffset.UtcNow);
        Assert.Null(MessageRules.CheckContent(good));
        Assert.NotNull(MessageRules.CheckContent(good with { Status = ReplyStatus.Done }));
        Assert.NotNull(MessageRules.CheckContent(good with { Type = (MessageType)99 }));
        Assert.NotNull(MessageRules.CheckContent(good with { ContentType = "plain" }));
        Assert.NotNull(MessageRules.CheckContent(good with { Subject = "" }));
        Assert.NotNull(MessageRules.CheckContent(good with { Metadata = new Dictionary<string, string> { ["bad key"] = "x" } }));
        Assert.NotNull(MessageRules.CheckContent(good with { Body = new string('x', Limits.MaxBodyBytes + 1) }));
    }

    [Fact]
    public void TypesAndStatusesTravelAsKebabCase()
    {
        Assert.Equal("file-receipt", WireProtocol.NameOf(MessageType.FileReceipt));
        Assert.Contains("\"type\":\"file-receipt\"", System.Text.Json.JsonSerializer.Serialize(
            Envelopes.Content(DateTimeOffset.UtcNow) with { Type = MessageType.FileReceipt }, WireProtocol.Json));
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
