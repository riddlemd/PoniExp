namespace PoniExp.Client;

public sealed class RelayClientOptions
{
    /// <summary>The router's base address, e.g. https://wojtek.example:5443.</summary>
    public required Uri RouterUrl { get; init; }

    /// <summary>
    /// SHA-256 (hex) of the router's TLS certificate. When set, that certificate is the only one accepted
    /// and the usual CA and host-name checks are skipped; when null, normal TLS validation applies.
    /// </summary>
    public string? PinnedCertificateSha256 { get; init; }

    /// <summary>Retry forever with these delays (the last one repeats). The relay is meant to stay connected.</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>
    /// How long one attempt to open the link (TCP, TLS and the WebSocket upgrade) may take. The socket has no
    /// limit of its own, so an attempt that a dead path never answers (after sleep, or a network change) would
    /// otherwise hang the reconnect loop for good.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// False keeps the router's copy of everything delivered: tests use it to play a client that crashes before
    /// confirming, and <c>doctor</c> and <c>join</c> for a probe connection whose deliveries the daemon must still get.
    /// </summary>
    public bool ConfirmMessages { get; init; } = true;

    /// <summary>For tests: route the connection through a given handler (e.g. a TestServer).</summary>
    public Func<HttpMessageHandler, HttpMessageHandler>? HttpMessageHandlerFactory { get; init; }
}
