using System.Security.Cryptography;

namespace PoniExp.Core.Crypto;

/// <summary>
/// A machine's encryption key (ECDH P-256), separate from its identity (signing) key. Messages to the
/// machine are encrypted to it; it is signed by the identity key, so the router can hand it out but not
/// swap in its own. Rotating it doesn't change who the machine is.
/// </summary>
public sealed class EncryptionKey : IDisposable
{
    private readonly ECDiffieHellman _key;

    private EncryptionKey(ECDiffieHellman key)
    {
        if (!PublicKeys.IsP256(key.ExportParameters(includePrivateParameters: false).Curve))
        {
            key.Dispose();
            throw new CryptographicException("An encryption key must be an ECDH P-256 key.");
        }

        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The public key as base64 SubjectPublicKeyInfo (DER).</summary>
    public string PublicKey { get; }

    public static EncryptionKey Create() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public static EncryptionKey FromPem(string pem)
    {
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return new EncryptionKey(key);
    }

    public string ExportPrivateKeyPem() => _key.ExportPkcs8PrivateKeyPem();

    /// <summary>The raw ECDH secret with another party's public key (an ephemeral one, when unwrapping).</summary>
    internal byte[] Agree(ECDiffieHellmanPublicKey other) => _key.DeriveRawSecretAgreement(other);

    public void Dispose() => _key.Dispose();
}
