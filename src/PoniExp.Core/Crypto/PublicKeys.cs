using System.Security.Cryptography;

namespace PoniExp.Core.Crypto;

public static class PublicKeys
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>True if <paramref name="signature"/> is a valid P-256 signature by <paramref name="publicKey"/> over <paramref name="data"/>.</summary>
    public static bool Verify(string publicKey, ReadOnlySpan<byte> data, string signature)
    {
        try
        {
            using var key = Import(publicKey);
            return key is not null && key.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>True if <paramref name="publicKey"/> is a well-formed base64 P-256 SubjectPublicKeyInfo.</summary>
    public static bool IsValid(string? publicKey)
    {
        if (string.IsNullOrEmpty(publicKey))
        {
            return false;
        }

        try
        {
            using var key = Import(publicKey);
            return key is not null;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    internal static bool IsP256(ECDsa key)
    {
        var curve = key.ExportParameters(includePrivateParameters: false).Curve;
        return curve.Oid?.Value == P256Oid || curve.Oid?.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1";
    }

    private static ECDsa? Import(string publicKey)
    {
        var der = Convert.FromBase64String(publicKey);
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(der, out var read);
        if (read != der.Length || !IsP256(key))
        {
            key.Dispose();
            return null;
        }

        return key;
    }
}
