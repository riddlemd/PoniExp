using Microsoft.Extensions.Options;

namespace PoniExp.Daemon;

/// <summary>
/// The daemon's settings: the "Daemon" section of settings.json (or PONIEXP_Daemon__* env vars). Read
/// through <see cref="IOptionsMonitor{T}"/> at the point of use, so edits apply without a restart, except
/// <see cref="OwnerSid"/>, which only matters when the local pipe is created.
/// </summary>
public sealed class DaemonOptions
{
    public const string Section = "Daemon";

    /// <summary>An endpoint that sends no heartbeat for this long is timed out.</summary>
    public TimeSpan EndpointTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How long automatic messages (receipts, endpoint announcements) live. They are never retried after.</summary>
    public TimeSpan AutomaticExpiry { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often the daemon checks for timed-out endpoints, expired messages and the outbox.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often an endpoint's stream gets a keep-alive comment.</summary>
    public TimeSpan StreamKeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait before trying the router again when it can't be reached at start.</summary>
    public TimeSpan ConnectRetry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// System mode on Windows: the SID of the user the daemon serves. Only that user (and the service) may
    /// open the local pipe. Unset in user mode, where the pipe is the current user's only.
    /// </summary>
    public string? OwnerSid { get; set; }
}

public sealed class DaemonOptionsValidator : IValidateOptions<DaemonOptions>
{
    public ValidateOptionsResult Validate(string? name, DaemonOptions options)
    {
        var errors = new List<string>();
        if (options.SweepInterval <= TimeSpan.Zero)
        {
            errors.Add("Daemon:SweepInterval must be positive.");
        }

        if (options.EndpointTimeout <= options.SweepInterval)
        {
            errors.Add("Daemon:EndpointTimeout must be longer than SweepInterval.");
        }

        if (options.AutomaticExpiry <= TimeSpan.Zero)
        {
            errors.Add("Daemon:AutomaticExpiry must be positive.");
        }

        if (options.StreamKeepAlive <= TimeSpan.Zero || options.ConnectRetry <= TimeSpan.Zero)
        {
            errors.Add("Daemon:StreamKeepAlive and ConnectRetry must be positive.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
