using System.Net;
using System.Net.Sockets;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Tests;

public class ReconnectTests
{
    [Fact]
    public async Task Given_AReconnectAttemptTheNetworkSwallows_When_TheNetworkRecovers_Then_TheClientReconnects()
    {
        // Given: an approved machine connected through a link that then drops, and whose next connect gets no answer
        await using var router = await RouterFixture.StartAsync();
        await using var proxy = TcpProxy.Start(router.Url);
        var (client, _) = await MemberThroughAsync(router, proxy, connectTimeout: TimeSpan.FromSeconds(1));
        var dropped = Wait.For<bool>(h => client.ConnectionChanged += (up, _) => { if (!up) h(true); });
        proxy.Swallow = true;
        proxy.DropAll();
        await dropped;
        await RouterFixture.Eventually(() => proxy.Swallowed > 0);

        // When: the network answers again
        proxy.Swallow = false;

        // Then: the swallowed attempt gives up and a later one gets through
        await RouterFixture.Eventually(() => client.IsConnected);
    }

    [Fact]
    public async Task Given_AnAnsweringRouter_When_ConnectTimeoutIsShort_Then_ItStillConnects()
    {
        // Given: a router that answers, and a connect timeout far shorter than the default
        await using var router = await RouterFixture.StartAsync();
        await using var proxy = TcpProxy.Start(router.Url);

        // When: the machine connects
        var (client, _) = await MemberThroughAsync(router, proxy, connectTimeout: TimeSpan.FromSeconds(1));

        // Then: the timeout bounds only the wait, not a connect that succeeds
        Assert.True(client.IsConnected);
    }

    private static async Task<(RelayClient Client, MachineKeys Keys)> MemberThroughAsync(RouterFixture router, TcpProxy proxy,
        TimeSpan connectTimeout)
    {
        var keys = MachineKeys.Create();
        var client = new RelayClient(keys, new RelayClientOptions
        {
            RouterUrl = proxy.Url,
            ReconnectDelays = [TimeSpan.FromMilliseconds(100)],
            ConnectTimeout = connectTimeout,
        }, new ClientStore(router.StorePath(keys)), MessagingSettings.Monitor(TestSettings.Create()));
        await client.ConnectAsync();
        var approved = Wait.For<MembershipStatus>(h => client.MembershipChanged += h);
        await client.RequestJoinAsync("ise", new MachineDescription("TestOS 1", "x64", "ise-host"));
        await router.Admin.ApproveAsync(keys.MachineId);
        Assert.Equal(MembershipStatus.Approved, await approved);
        await RouterFixture.Eventually(() => client.IsConnected);
        return (client, keys);
    }

    /// <summary>
    /// Forwards loopback TCP to the router. <see cref="Swallow"/> plays a dead network path: connections are
    /// accepted and read, but nothing is forwarded or answered, the way a stalled TLS or upgrade looks.
    /// </summary>
    private sealed class TcpProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Uri _target;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<TcpClient> _open = [];
        private readonly Task _accepting;
        private int _swallowed;

        private TcpProxy(Uri target)
        {
            _target = target;
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public static TcpProxy Start(Uri target) => new(target);

        public Uri Url => new($"{_target.Scheme}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public volatile bool Swallow;

        public int Swallowed => Volatile.Read(ref _swallowed);

        /// <summary>Resets every open connection, as a dropped link does.</summary>
        public void DropAll()
        {
            lock (_open)
            {
                foreach (var c in _open)
                {
                    try
                    {
                        c.Client.LingerState = new LingerOption(true, 0);
                    }
                    catch (Exception)
                    {
                        // already closed by its other side
                    }

                    c.Dispose();
                }

                _open.Clear();
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient inbound;
                try
                {
                    inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (_open)
                {
                    _open.Add(inbound);
                }

                _ = Swallow ? SwallowAsync(inbound) : ForwardAsync(inbound);
            }
        }

        private async Task SwallowAsync(TcpClient inbound)
        {
            Interlocked.Increment(ref _swallowed);
            try
            {
                await inbound.GetStream().CopyToAsync(Stream.Null, _stop.Token);
            }
            catch (Exception)
            {
            }
        }

        private async Task ForwardAsync(TcpClient inbound)
        {
            var outbound = new TcpClient();
            try
            {
                await outbound.ConnectAsync(_target.Host, _target.Port, _stop.Token);
                lock (_open)
                {
                    _open.Add(outbound);
                }

                var a = inbound.GetStream();
                var b = outbound.GetStream();
                await Task.WhenAny(a.CopyToAsync(b, _stop.Token), b.CopyToAsync(a, _stop.Token));
            }
            catch (Exception)
            {
            }
            finally
            {
                inbound.Dispose();
                outbound.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            DropAll();
            await _accepting;
        }
    }
}
