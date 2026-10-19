using Microsoft.Extensions.Options;

namespace PoniExp.Server;

/// <summary>
/// Bound from the "PoniExp" configuration section (e.g. the env var PoniExp__Port). The listener and data
/// settings are read once at startup; the rest is read through <see cref="IOptionsMonitor{T}"/> (singletons)
/// or <see cref="IOptionsSnapshot{T}"/> (the hub), so a configuration reload applies without a restart.
/// </summary>
public sealed class RouterOptions
{
    public const string Section = "PoniExp";

    /// <summary>Where machines.json, queue.json and the TLS certificate live. A volume in the container. Startup only.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Startup only.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Startup only.</summary>
    public int Port { get; set; } = 5443;

    /// <summary>Serve the relay over TLS with the router's self-signed certificate (clients pin its SHA-256). Startup only.</summary>
    public bool UseHttps { get; set; } = true;

    /// <summary>Plain-HTTP admin listener on 127.0.0.1 only (reach it with docker exec). Null to skip it. Startup only.</summary>
    public int? AdminPort { get; set; } = 5081;

    /// <summary>
    /// The longest a message is delivered for, counted from when the router received it. A later
    /// <c>expiresOn</c> is treated as this.
    /// </summary>
    public TimeSpan MaxExpiry { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How far a signed timestamp (a join request) may be from the router's clock.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Caps unapproved join requests, so nobody can fill the list.</summary>
    public int MaxPendingMachines { get; set; } = 50;
}

public sealed class RouterOptionsValidator : IValidateOptions<RouterOptions>
{
    public ValidateOptionsResult Validate(string? name, RouterOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            errors.Add("PoniExp:DataDirectory is required.");
        }

        if (options.Port is < 0 or > 65535)
        {
            errors.Add("PoniExp:Port must be 0-65535.");
        }

        if (options.MaxExpiry <= TimeSpan.Zero)
        {
            errors.Add("PoniExp:MaxExpiry must be positive.");
        }

        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew >= options.MaxExpiry)
        {
            errors.Add("PoniExp:ClockSkew must be zero or more, and less than MaxExpiry.");
        }

        if (options.MaxPendingMachines < 1)
        {
            errors.Add("PoniExp:MaxPendingMachines must be at least 1.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
