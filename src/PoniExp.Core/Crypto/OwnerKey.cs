using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PoniExp.Core.Crypto;

/// <summary>
/// The owner's SSH public key (an <c>ssh-ed25519</c> line, e.g. "ssh-ed25519 AAAA... agent-relay owner"), its fingerprint as
/// <c>ssh-add -l</c> shows it, and checks of SSH signatures it made (the <c>ssh-keygen -Y sign</c> preimage, namespace
/// <see cref="Namespace"/>). PoniExp never holds the private key: it stays in the SSH agent (e.g. Bitwarden's).
/// </summary>
public sealed class OwnerKey
{
    public const string Namespace = "poniexp";
    public const string KeyType = "ssh-ed25519";

    private OwnerKey(string line, byte[] blob, byte[] publicKey, string? comment)
    {
        Line = line;
        Blob = blob;
        PublicKey = publicKey;
        Comment = comment;
    }

    /// <summary>The key as "ssh-ed25519 base64" (no comment): what is stored and compared.</summary>
    public string Line { get; }

    /// <summary>The SSH wire blob (string type, string key): what an SSH agent identifies the key by.</summary>
    public byte[] Blob { get; }

    public byte[] PublicKey { get; }

    public string? Comment { get; }

    /// <summary>"SHA256:..." as <c>ssh-add -l</c> and <c>ssh-keygen -lf</c> show it.</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');

    /// <exception cref="FormatException">Not an ssh-ed25519 public key line.</exception>
    public static OwnerKey Parse(string text)
    {
        var parts = text.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != KeyType)
        {
            throw new FormatException("the owner key is an ssh-ed25519 public key line (as `ssh-add -L` prints it)");
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            throw new FormatException("the owner key's base64 is damaged");
        }

        return FromBlob(blob, parts.Length > 2 ? parts[2] : null);
    }

    public static OwnerKey FromBlob(byte[] blob, string? comment = null)
    {
        var reader = new SshReader(blob);
        if (reader.String() is not KeyType || reader.Bytes() is not { Length: 32 } key || !reader.AtEnd)
        {
            throw new FormatException("not an ssh-ed25519 key");
        }

        return new OwnerKey($"{KeyType} {Convert.ToBase64String(blob)}", blob, key, comment);
    }

    /// <summary>What the SSH agent is asked to sign for <paramref name="message"/>: the SSHSIG preimage (sha512).</summary>
    public static byte[] SignedData(byte[] message)
    {
        var writer = new SshWriter();
        writer.Raw("SSHSIG"u8);
        writer.String(Namespace);
        writer.String("");
        writer.String("sha512");
        writer.Bytes(SHA512.HashData(message));
        return writer.ToArray();
    }

    /// <summary>Checks an SSH agent's signature (the blob: string "ssh-ed25519", string 64 bytes) over <paramref name="message"/>.</summary>
    public bool Verify(byte[] message, string signature)
    {
        try
        {
            var reader = new SshReader(Convert.FromBase64String(signature));
            if (reader.String() != KeyType || reader.Bytes() is not { Length: 64 } sig || !reader.AtEnd)
            {
                return false;
            }

            var data = SignedData(message);
            return Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Verify(sig, 0, PublicKey, 0, data, 0, data.Length);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads an armored <c>ssh-keygen -Y sign</c> signature ("-----BEGIN SSH SIGNATURE-----") into the
    /// signature blob <see cref="Verify"/> takes, checking it's this key's, in <see cref="Namespace"/>, over sha512.
    /// </summary>
    public string? FromArmored(string armored)
    {
        try
        {
            var body = string.Concat(armored.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
            var reader = new SshReader(Convert.FromBase64String(body));
            var magic = new byte[6];
            for (var i = 0; i < 6; i++)
            {
                magic[i] = reader.Byte();
            }

            if (!"SSHSIG"u8.SequenceEqual(magic) || reader.UInt32() != 1 || !reader.Bytes().AsSpan().SequenceEqual(Blob)
                || reader.String() != Namespace || reader.String() != "" || reader.String() != "sha512")
            {
                return null;
            }

            return Convert.ToBase64String(reader.Bytes());
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public bool SameKeyAs(OwnerKey other) => Blob.AsSpan().SequenceEqual(other.Blob);

    public override string ToString() => Comment is null ? Line : $"{Line} {Comment}";
}

/// <summary>SSH wire encoding: big-endian uint32 lengths before strings.</summary>
public sealed class SshReader(byte[] data)
{
    private int _at;

    public bool AtEnd => _at == data.Length;

    public uint UInt32()
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_at, 4));
        _at += 4;
        return value;
    }

    public byte Byte() => data[_at++];

    public byte[] Bytes()
    {
        var length = (int)UInt32();
        if (length < 0 || _at + length > data.Length)
        {
            throw new FormatException("a truncated SSH string");
        }

        var value = data.AsSpan(_at, length).ToArray();
        _at += length;
        return value;
    }

    public string String() => Encoding.UTF8.GetString(Bytes());
}

public sealed class SshWriter
{
    private readonly MemoryStream _buffer = new();

    public void Raw(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

    public void Byte(byte value) => _buffer.WriteByte(value);

    public void UInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _buffer.Write(bytes);
    }

    public void Bytes(ReadOnlySpan<byte> value)
    {
        UInt32((uint)value.Length);
        _buffer.Write(value);
    }

    public void String(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    public byte[] ToArray() => _buffer.ToArray();
}
