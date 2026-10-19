namespace PoniExp.Router;

/// <summary>Bound from the "PoniExp" configuration section (e.g. the env var PoniExp__Port).</summary>
public sealed class RouterOptions
{
    public const string Section = "PoniExp";

    /// <summary>Where machines.json, queue.json and the TLS certificate live. A volume in the container.</summary>
    public string DataDirectory { get; set; } = "data";

    public string BindAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 5443;

    /// <summary>Serve the relay over TLS with the router's self-signed certificate (clients pin its SHA-256).</summary>
    public bool UseHttps { get; set; } = true;

    /// <summary>Plain-HTTP admin listener on 127.0.0.1 only (reach it with docker exec). Null to skip it.</summary>
    public int? AdminPort { get; set; } = 5081;

    /// <summary>How long an envelope waits for an offline recipient before it's dropped.</summary>
    public TimeSpan QueueRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Caps unapproved join requests, so nobody can fill the list.</summary>
    public int MaxPendingMachines { get; set; } = 50;
}
