using System.Buffers.Binary;
using System.Net.Sockets;
using PoniExp.Core.Crypto;

namespace PoniExp.Tests;

/// <summary>
/// An SSH agent (draft-miller-ssh-agent) on a Unix socket, holding a <see cref="TestOwner"/>'s key: what Bitwarden's agent
/// is to the real CLI. Point a <c>poniexp-client</c> process at it with <c>SSH_AUTH_SOCK=<see cref="SocketPath"/></c>.
/// </summary>
public sealed class FakeSshAgent : IAsyncDisposable
{
    private const byte Failure = 5;
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    private readonly TestOwner _owner;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private int _signed;

    private FakeSshAgent(TestOwner owner)
    {
        _owner = owner;
        SocketPath = Path.Combine("/tmp", $"pxagent-{Guid.NewGuid().ToString("N")[..10]}.sock"); // short: sun_path is 104 bytes on macOS
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(8);
        _serving = Task.Run(ServeAsync);
    }

    public string SocketPath { get; }

    /// <summary>How many signing requests the agent answered.</summary>
    public int Signed => Volatile.Read(ref _signed);

    public static FakeSshAgent Start(TestOwner owner) => new(owner);

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(connection));
        }
    }

    private async Task AnswerAsync(Socket connection)
    {
        await using var stream = new NetworkStream(connection, ownsSocket: true);
        try
        {
            while (true)
            {
                var length = new byte[4];
                await stream.ReadExactlyAsync(length, _stop.Token);
                var request = new byte[BinaryPrimitives.ReadUInt32BigEndian(length)];
                await stream.ReadExactlyAsync(request, _stop.Token);
                var reply = Answer(request);
                var frame = new byte[4 + reply.Length];
                BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)reply.Length);
                reply.CopyTo(frame, 4);
                await stream.WriteAsync(frame, _stop.Token);
            }
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or OperationCanceledException)
        {
            // the client is done
        }
    }

    private byte[] Answer(byte[] request)
    {
        var reader = new SshReader(request);
        var writer = new SshWriter();
        switch (reader.Byte())
        {
            case RequestIdentities:
                writer.Byte(IdentitiesAnswer);
                writer.UInt32(1);
                writer.Bytes(_owner.Key.Blob);
                writer.String("test owner");
                break;
            case SignRequest when reader.Bytes().AsSpan().SequenceEqual(_owner.Key.Blob):
                var data = reader.Bytes();
                writer.Byte(SignResponse);
                writer.Bytes(_owner.SignatureBlob(data));
                Interlocked.Increment(ref _signed);
                break;
            default:
                writer.Byte(Failure);
                break;
        }

        return writer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        await _serving;
        File.Delete(SocketPath);
        _stop.Dispose();
    }
}
