using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoniExp.Core.Protocol;

namespace PoniExp.Core.Files;

/// <summary>
/// How a file becomes a blob (see docs/plan.md, Phase 6). One random 256-bit key per upload. The file is cut
/// into <see cref="ChunkSize"/> chunks, each sealed with AES-256-GCM; the nonce is the chunk's index, and the
/// blob id, the index and a last-chunk flag are its associated data, so the router can't reorder, drop, swap
/// or truncate chunks unnoticed. An encrypted manifest holds the name, content type, real size and every
/// chunk's hash; the message that carries the file pins the manifest's chunk list by its hash.
/// </summary>
public static class BlobCrypto
{
    public const int ChunkSize = 4 * 1024 * 1024;
    public const int TagSize = 16;
    public const int KeySize = 32;

    /// <summary>The most a sealed chunk can be.</summary>
    public const int MaxSealedChunk = ChunkSize + TagSize;

    /// <summary>The most a sealed manifest may be (2 GB of chunks needs about 40 KB).</summary>
    public const int MaxSealedManifest = 1024 * 1024;

    private static readonly byte[] Domain = "poniexp-blob-v1"u8.ToArray();
    private const byte ChunkKind = 0;
    private const byte ManifestKind = 1;

    /// <summary>At least one chunk: an empty file is one empty last chunk.</summary>
    public static int ChunkCountOf(long size) => size == 0 ? 1 : checked((int)((size + ChunkSize - 1) / ChunkSize));

    /// <summary>What a blob takes on the router: its chunks with their tags, and room for the manifest.</summary>
    public static long EncryptedSizeOf(long size)
    {
        var chunks = ChunkCountOf(size);
        return size + (long)chunks * TagSize + 4096 + chunks * 96L;
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static bool IsValidKey(string? key)
    {
        try
        {
            return key is not null && Convert.FromBase64String(key).Length == KeySize;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A plain file name: no directories, nothing a file system would read specially.</summary>
    public static bool IsValidFileName(string? name) =>
        name is { Length: > 0 and <= Limits.MaxFileNameLength } && name.Trim() == name && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 && !name.Any(char.IsControl);

    public static byte[] SealChunk(byte[] key, Guid blobId, int index, bool last, ReadOnlySpan<byte> plain)
    {
        var sealedChunk = new byte[plain.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(Nonce(ChunkKind, index), plain, sealedChunk.AsSpan(0, plain.Length), sealedChunk.AsSpan(plain.Length), Aad(ChunkKind, blobId, index, last));
        return sealedChunk;
    }

    /// <exception cref="CryptographicException">The chunk was tampered with, or isn't chunk <paramref name="index"/> (last or not) of this blob.</exception>
    public static byte[] OpenChunk(byte[] key, Guid blobId, int index, bool last, ReadOnlySpan<byte> sealedChunk)
    {
        if (sealedChunk.Length < TagSize)
        {
            throw new CryptographicException("A chunk is shorter than its tag.");
        }

        var plain = new byte[sealedChunk.Length - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(Nonce(ChunkKind, index), sealedChunk[..plain.Length], sealedChunk[plain.Length..], plain, Aad(ChunkKind, blobId, index, last));
        return plain;
    }

    public static byte[] SealManifest(byte[] key, Guid blobId, BlobManifest manifest)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(manifest, WireProtocol.Json);
        var sealedManifest = new byte[plain.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(Nonce(ManifestKind, 0), plain, sealedManifest.AsSpan(0, plain.Length), sealedManifest.AsSpan(plain.Length), Aad(ManifestKind, blobId, 0, true));
        return sealedManifest;
    }

    /// <exception cref="CryptographicException">Tampered with, or another blob's.</exception>
    public static BlobManifest OpenManifest(byte[] key, Guid blobId, ReadOnlySpan<byte> sealedManifest)
    {
        if (sealedManifest.Length < TagSize)
        {
            throw new CryptographicException("The manifest is shorter than its tag.");
        }

        var plain = new byte[sealedManifest.Length - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(Nonce(ManifestKind, 0), sealedManifest[..plain.Length], sealedManifest[plain.Length..], plain, Aad(ManifestKind, blobId, 0, true));
        return JsonSerializer.Deserialize<BlobManifest>(plain, WireProtocol.Json) ?? throw new CryptographicException("An empty manifest.");
    }

    /// <summary>Lowercase hex SHA-256.</summary>
    public static string HashOf(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The hash the message carries: over the chunk hashes, in order.</summary>
    public static string ChunksHashOf(IEnumerable<string> chunkHashes) => HashOf(Encoding.ASCII.GetBytes(string.Join('\n', chunkHashes)));

    private static byte[] Nonce(byte kind, int index)
    {
        var nonce = new byte[12];
        nonce[0] = kind;
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    private static byte[] Aad(byte kind, Guid blobId, int index, bool last)
    {
        var aad = new byte[Domain.Length + 1 + 16 + 4 + 1];
        Domain.CopyTo(aad, 0);
        aad[Domain.Length] = kind;
        blobId.TryWriteBytes(aad.AsSpan(Domain.Length + 1, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(Domain.Length + 17), index);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }
}

/// <summary>A blob's manifest, sealed with its file key: what the recipient checks the message's attachment against.</summary>
public sealed record BlobManifest(string Name, string ContentType, long Size, int ChunkCount, IReadOnlyList<string> ChunkHashes);
