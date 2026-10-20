using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

/// <summary>An owner key for tests, signing as an SSH agent would (the real one lives in Bitwarden).</summary>
public sealed class TestOwner : IOwnerSigner
{
    private readonly Ed25519PrivateKeyParameters _private;

    private TestOwner(Ed25519PrivateKeyParameters key)
    {
        _private = key;
        var writer = new SshWriter();
        writer.String(OwnerKey.KeyType);
        writer.Bytes(key.GeneratePublicKey().GetEncoded());
        Key = OwnerKey.FromBlob(writer.ToArray(), "test owner");
    }

    public OwnerKey Key { get; }

    public int Signed { get; private set; }

    public static TestOwner Create() => new(new Ed25519PrivateKeyParameters(new SecureRandom()));

    public Task<string> SignAsync(OwnerKey key, byte[] message, CancellationToken ct)
    {
        if (!key.SameKeyAs(Key))
        {
            throw new OwnerKeyUnavailableException("the SSH agent doesn't hold the owner's key");
        }

        Signed++;
        var data = OwnerKey.SignedData(message);
        var signature = new byte[64];
        _private.Sign(Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Algorithm.Ed25519, null, data, 0, data.Length, signature, 0);
        var writer = new SshWriter();
        writer.String(OwnerKey.KeyType);
        writer.Bytes(signature);
        return Task.FromResult(Convert.ToBase64String(writer.ToArray()));
    }

    public Task<IReadOnlyList<OwnerKey>> KeysAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<OwnerKey>>([Key]);

    /// <summary>An admin command for <paramref name="routerId"/>, signed by this owner.</summary>
    public AdminCommand Admin(string routerId, string[] args, string? input = null, DateTimeOffset? issued = null)
    {
        var id = Guid.NewGuid();
        var on = issued ?? DateTimeOffset.UtcNow;
        return new AdminCommand(id, routerId, args, input, on, SignAsync(Key, AdminCommand.Payload(id, routerId, args, input, on), default).Result);
    }

    public HereClaim Here(string? machine, DateTimeOffset issued, DateTimeOffset expires)
    {
        var payload = HereClaim.Payload(machine, issued, expires);
        return new HereClaim(machine, issued, expires, SignAsync(Key, payload, default).Result);
    }
}
