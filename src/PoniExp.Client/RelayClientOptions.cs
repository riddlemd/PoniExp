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

    /// <summary>For tests: route the connection through a given handler (e.g. a TestServer).</summary>
    public Func<HttpMessageHandler, HttpMessageHandler>? HttpMessageHandlerFactory { get; init; }
}
