using PoniExp.Core.Protocol;
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

    /// <summary>An agent that sends no heartbeat for this long is timed out.</summary>
    public TimeSpan AgentTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How long automatic messages (receipts, agent announcements) live. They are never retried after.</summary>
    public TimeSpan AutomaticExpiry { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often the daemon checks for timed-out agents, expired messages and the outbox.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often an agent's stream gets a keep-alive comment.</summary>
    public TimeSpan StreamKeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait before trying the router again when it can't be reached at start.</summary>
    public TimeSpan ConnectRetry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the leases of jobs held by online agents are renewed (keep it well under the router's JobLease).</summary>
    public TimeSpan JobLeaseRenewal { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Tags for what detection can't see (e.g. "docker", "cuda"), matched by job requirements like <c>tag=docker</c>.
    /// Set as Daemon:CapabilityTags:0, :1, ...
    /// </summary>
    public List<string> CapabilityTags { get; set; } = [];

    /// <summary>How long a room invitation stays open (at most 30 days).</summary>
    public TimeSpan RoomInviteLifetime { get; set; } = TimeSpan.FromDays(7);

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
        if (options.CapabilityTags.Count > 32 || options.CapabilityTags.Any(t => !Limits.IsValidAgent(t)))
        {
            errors.Add($"Daemon:CapabilityTags: at most 32, each {Limits.AgentRule}.");
        }

        if (options.JobLeaseRenewal <= TimeSpan.Zero)
        {
            errors.Add("Daemon:JobLeaseRenewal must be positive.");
        }

        if (options.RoomInviteLifetime <= TimeSpan.Zero || options.RoomInviteLifetime > RoomLog.MaxInviteLifetime)
        {
            errors.Add("Daemon:RoomInviteLifetime must be positive and at most 30 days.");
        }

        if (options.SweepInterval <= TimeSpan.Zero)
        {
            errors.Add("Daemon:SweepInterval must be positive.");
        }

        if (options.AgentTimeout <= options.SweepInterval)
        {
            errors.Add("Daemon:AgentTimeout must be longer than SweepInterval.");
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
