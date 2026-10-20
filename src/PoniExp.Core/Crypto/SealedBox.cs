using System.Security.Cryptography;

namespace PoniExp.Core.Crypto;

/// <summary>
/// Data sealed to one reader's encryption key: ephemeral-static ECDH P-256, HKDF-SHA256 (salt: the ephemeral public key;
/// info: the purpose and the reader), AES-256-GCM with the same binding as associated data. Used for admin commands
/// (to the router) and their results (to the asking machine).
/// </summary>
public sealed record SealedBox(string EphemeralKey, string Ciphertext)
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static SealedBox Seal(byte[] plaintext, string purpose, string readerId, string readerEncryptionKey)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var reader = PublicKeys.ImportEncryptionKey(readerEncryptionKey) ?? throw new CryptographicException("Not a P-256 encryption key.");
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var key = Derive(ephemeral.DeriveRawSecretAgreement(reader.PublicKey), ephemeralPublic, purpose, readerId);
        try
        {
            var output = new byte[NonceSize + plaintext.Length + TagSize];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(output.AsSpan(0, NonceSize), plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length),
                Binding(purpose, readerId));
            return new SealedBox(Convert.ToBase64String(ephemeralPublic), Convert.ToBase64String(output));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <exception cref="CryptographicException">Not sealed to this reader for this purpose, or altered.</exception>
    public byte[] Open(string purpose, string readerId, EncryptionKey readerKey)
    {
        try
        {
            var ephemeralPublic = Convert.FromBase64String(EphemeralKey);
            var data = Convert.FromBase64String(Ciphertext);
            using var ephemeral = PublicKeys.ImportEncryptionKey(EphemeralKey) ?? throw new CryptographicException("The ephemeral key isn't a P-256 key.");
            if (data.Length < NonceSize + TagSize)
            {
                throw new CryptographicException("The ciphertext is too short.");
            }

            var key = Derive(readerKey.Agree(ephemeral.PublicKey), ephemeralPublic, purpose, readerId);
            try
            {
                var plaintext = new byte[data.Length - NonceSize - TagSize];
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, plaintext.Length), data.AsSpan(NonceSize + plaintext.Length), plaintext,
                    Binding(purpose, readerId));
                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new CryptographicException("The sealed box is malformed.", e);
        }
    }

    private static byte[] Binding(string purpose, string readerId) => SigningPayload.Build("box", purpose, readerId);

    private static byte[] Derive(byte[] secret, byte[] ephemeralPublic, string purpose, string readerId)
    {
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, salt: ephemeralPublic, info: Binding(purpose, readerId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
