using System.Security.Cryptography;

namespace PoniExp.Core.Crypto;

/// <summary>
/// A machine's private key (ECDSA P-256). It never leaves the machine: the router and the other
/// machines only ever see <see cref="PublicKey"/>, and the machine id is a hash of that key.
/// </summary>
public sealed class MachineIdentity : IDisposable
{
    private readonly ECDsa _key;

    private MachineIdentity(ECDsa key)
    {
        if (!PublicKeys.IsP256(key))
        {
            key.Dispose();
            throw new CryptographicException("A machine key must be an ECDSA P-256 key.");
        }

        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        MachineId = KeyFingerprint.MachineIdOf(PublicKey);
    }

    /// <summary>The public key as base64 SubjectPublicKeyInfo (DER).</summary>
    public string PublicKey { get; }

    /// <summary>32 hex characters: the first 128 bits of SHA-256 over the public key.</summary>
    public string MachineId { get; }

    public static MachineIdentity Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static MachineIdentity FromPem(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return new MachineIdentity(key);
    }

    public string ExportPrivateKeyPem() => _key.ExportPkcs8PrivateKeyPem();

    /// <summary>Signs <paramref name="data"/> (SHA-256, IEEE P1363 r||s) and returns the signature as base64.</summary>
    public string Sign(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => _key.Dispose();
}
