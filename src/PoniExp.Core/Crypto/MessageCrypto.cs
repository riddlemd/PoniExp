using System.Security.Cryptography;
using System.Text.Json;
using PoniExp.Core.Protocol;

namespace PoniExp.Core.Crypto;

/// <summary>
/// End-to-end encryption of message content.
/// <list type="bullet">
/// <item>A random 256-bit content key per message; the content (JSON) is sealed with AES-256-GCM.</item>
/// <item>The content key is wrapped once per reader (the recipient, and the sender's own machine so it can
/// read what it sent) with ephemeral-static ECDH P-256: a fresh key pair per wrap, HKDF-SHA256 over the
/// shared secret, then AES-256-GCM. No two wraps ever use the same key.</item>
/// <item>The router-visible header (id, from, to, sequence, expiry) is the AES-GCM associated data of both
/// the content and every wrap, and the message id, sender and reader are in the HKDF info. Re-signing
/// someone else's ciphertext as another sender, or editing the header, makes it fail to decrypt.</item>
/// </list>
/// The sender signs the result (ciphertext and wraps included), so tampering is caught before decryption too.
/// </summary>
public static class MessageCrypto
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>One reader: a machine id and its (already verified) encryption public key.</summary>
    public readonly record struct Reader(string MachineId, string EncryptionKey);

    public sealed record Sealed(string Ciphertext, int Size, IReadOnlyList<KeyWrap> Wraps);

    public static Sealed Seal(MessageContent content, Guid id, string fromMachine, string toMachine, long sequence, DateTimeOffset expiresOn,
        IEnumerable<Reader> readers)
    {
        var aad = AssociatedData(id, fromMachine, toMachine, sequence, expiresOn);
        var contentKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var ciphertext = Encrypt(contentKey, JsonSerializer.SerializeToUtf8Bytes(content, WireProtocol.Json), aad);
            var wraps = readers.DistinctBy(r => r.MachineId).Select(reader =>
            {
                using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                using var readerKey = PublicKeys.ImportEncryptionKey(reader.EncryptionKey)
                                      ?? throw new CryptographicException($"Not a P-256 encryption key for {reader.MachineId}.");
                var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
                var kek = WrappingKey(ephemeral.DeriveRawSecretAgreement(readerKey.PublicKey), ephemeralPublic, id, fromMachine, reader.MachineId);
                return new KeyWrap(reader.MachineId, Convert.ToBase64String(ephemeralPublic), Convert.ToBase64String(Encrypt(kek, contentKey, aad)));
            }).ToList();
            return new Sealed(Convert.ToBase64String(ciphertext), ciphertext.Length, wraps);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    /// <summary>Opens an envelope with this machine's encryption key. Throws <see cref="CryptographicException"/> if it can't.</summary>
    public static MessageContent Open(Envelope envelope, string myMachineId, EncryptionKey myKey)
    {
        var wrap = envelope.Wraps?.FirstOrDefault(w => w.Machine == myMachineId)
                   ?? throw new CryptographicException("The content key isn't wrapped for this machine.");
        var aad = AssociatedData(envelope.Id, envelope.FromMachine, envelope.ToMachine, envelope.Sequence, envelope.ExpiresOn);
        try
        {
            var ephemeralPublic = Convert.FromBase64String(wrap.EphemeralKey);
            using var ephemeral = PublicKeys.ImportEncryptionKey(wrap.EphemeralKey)
                                  ?? throw new CryptographicException("The wrap's ephemeral key isn't a P-256 key.");
            var kek = WrappingKey(myKey.Agree(ephemeral.PublicKey), ephemeralPublic, envelope.Id, envelope.FromMachine, myMachineId);
            var contentKey = Decrypt(kek, Convert.FromBase64String(wrap.WrappedKey), aad);
            try
            {
                var plaintext = Decrypt(contentKey, Convert.FromBase64String(envelope.Ciphertext), aad);
                return JsonSerializer.Deserialize<MessageContent>(plaintext, WireProtocol.Json)
                       ?? throw new CryptographicException("The content is empty.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contentKey);
            }
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            throw new CryptographicException("The envelope is malformed.", e);
        }
    }

    private static byte[] AssociatedData(Guid id, string fromMachine, string toMachine, long sequence, DateTimeOffset expiresOn) =>
        SigningPayload.Build("aad", id.ToString("D"), fromMachine, toMachine,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), Signatures.Stamp(expiresOn));

    private static byte[] WrappingKey(byte[] sharedSecret, byte[] ephemeralPublic, Guid id, string fromMachine, string readerMachine)
    {
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, KeySize, salt: ephemeralPublic,
                info: SigningPayload.Build("wrap", id.ToString("D"), fromMachine, readerMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    // nonce || ciphertext || tag
    private static byte[] Encrypt(byte[] key, byte[] plaintext, byte[] aad)
    {
        var output = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize, plaintext.Length), output.AsSpan(NonceSize + plaintext.Length), aad);
        return output;
    }

    private static byte[] Decrypt(byte[] key, byte[] sealedData, byte[] aad)
    {
        if (sealedData.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The ciphertext is too short.");
        }

        var plaintext = new byte[sealedData.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedData.AsSpan(0, NonceSize), sealedData.AsSpan(NonceSize, plaintext.Length),
            sealedData.AsSpan(NonceSize + plaintext.Length), plaintext, aad);
        return plaintext;
    }
}
