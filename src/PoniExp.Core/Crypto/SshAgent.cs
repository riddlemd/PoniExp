using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;

namespace PoniExp.Core.Crypto;

/// <summary>Signs with the owner's key: in use, the SSH agent (<see cref="SshAgent"/>); in tests, a key in memory.</summary>
public interface IOwnerSigner
{
    /// <summary>An SSH signature blob over the SSHSIG preimage of <paramref name="message"/> (<see cref="OwnerKey.SignedData"/>).</summary>
    /// <exception cref="OwnerKeyUnavailableException">No agent, the agent lacks the key, or the owner said no.</exception>
    Task<string> SignAsync(OwnerKey key, byte[] message, CancellationToken ct);

    /// <summary>The ssh-ed25519 keys the agent holds.</summary>
    Task<IReadOnlyList<OwnerKey>> KeysAsync(CancellationToken ct);
}

public sealed class OwnerKeyUnavailableException(string message) : Exception(message);

/// <summary>
/// A client for the SSH agent protocol (draft-miller-ssh-agent): on Windows the OpenSSH agent pipe (Bitwarden
/// serves it), elsewhere the socket in SSH_AUTH_SOCK. Signing may wait on the agent's own prompt (Bitwarden's
/// unlock or approval), so callers give a generous timeout.
/// </summary>
public sealed class SshAgent(string? endpoint = null) : IOwnerSigner
{
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    /// <summary>Where the agent listens here: SSH_AUTH_SOCK if set, else on Windows the OpenSSH pipe.</summary>
    public static string? DefaultEndpoint =>
        Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } sock ? sock
        : OperatingSystem.IsWindows() ? @"\\.\pipe\openssh-ssh-agent" : null;

    public async Task<IReadOnlyList<OwnerKey>> KeysAsync(CancellationToken ct)
    {
        var reply = await RoundTripAsync([RequestIdentities], ct);
        var reader = new SshReader(reply);
        if (reader.Byte() != IdentitiesAnswer)
        {
            throw new OwnerKeyUnavailableException("the SSH agent wouldn't list its keys");
        }

        var keys = new List<OwnerKey>();
        for (var n = reader.UInt32(); n > 0; n--)
        {
            var blob = reader.Bytes();
            var comment = reader.String();
            try
            {
                keys.Add(OwnerKey.FromBlob(blob, comment));
            }
            catch (FormatException)
            {
                // not ed25519
            }
        }

        return keys;
    }

    public async Task<string> SignAsync(OwnerKey key, byte[] message, CancellationToken ct)
    {
        var writer = new SshWriter();
        writer.Byte(SignRequest);
        writer.Bytes(key.Blob);
        writer.Bytes(OwnerKey.SignedData(message));
        writer.UInt32(0);
        var reply = await RoundTripAsync(writer.ToArray(), ct);
        var reader = new SshReader(reply);
        if (reader.Byte() != SignResponse)
        {
            throw new OwnerKeyUnavailableException("the SSH agent didn't sign (the key isn't there, or the request was declined)");
        }

        return Convert.ToBase64String(reader.Bytes());
    }

    private async Task<byte[]> RoundTripAsync(byte[] request, CancellationToken ct)
    {
        var where = endpoint ?? DefaultEndpoint ?? throw new OwnerKeyUnavailableException("no SSH agent (SSH_AUTH_SOCK isn't set)");
        await using var stream = await ConnectAsync(where, ct);
        var frame = new byte[4 + request.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)request.Length);
        request.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, ct);
        var size = BinaryPrimitives.ReadUInt32BigEndian(length);
        if (size is 0 or > 256 * 1024)
        {
            throw new OwnerKeyUnavailableException("the SSH agent sent a malformed reply");
        }

        var reply = new byte[size];
        await stream.ReadExactlyAsync(reply, ct);
        return reply;
    }

    private static async Task<Stream> ConnectAsync(string where, CancellationToken ct)
    {
        try
        {
            if (where.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var pipe = new NamedPipeClientStream(".", where[(where.LastIndexOf('\\') + 1)..], PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, ct);
                return pipe;
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(where), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception e) when (e is IOException or TimeoutException or SocketException or UnauthorizedAccessException)
        {
            throw new OwnerKeyUnavailableException($"can't reach the SSH agent at {where} ({e.Message})");
        }
    }
}
