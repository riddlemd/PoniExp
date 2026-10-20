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

    /// <summary>Where router.db and the TLS certificate live. A volume in the container. Startup only.</summary>
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

    /// <summary>A join request not approved or rejected within this long is forgotten (the machine may ask again).</summary>
    public TimeSpan JoinRequestExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>New join requests the router accepts per hour, from anywhere.</summary>
    public int JoinRequestsPerHour { get; set; } = 10;

    /// <summary>New join requests per hour from one address (not applied to <see cref="SharedAddresses"/>).</summary>
    public int JoinRequestsPerHourPerAddress { get; set; } = 5;

    /// <summary>
    /// How long a connection may go without proving a key that is approved or pending. A <c>join</c> waiting
    /// for approval is pending, so it may stay.
    /// </summary>
    public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Connections that haven't proved an approved or pending key, at once, from anywhere.</summary>
    public int MaxUnauthenticatedConnections { get; set; } = 20;

    /// <summary>Of those, from one address (not applied to <see cref="SharedAddresses"/>).</summary>
    public int MaxUnauthenticatedPerAddress { get; set; } = 10;

    /// <summary>Connections one approved or pending machine may hold at once.</summary>
    public int MaxConnectionsPerMachine { get; set; } = 5;

    /// <summary>
    /// Addresses that stand for many clients, so per-address limits would lump them together: e.g. a port
    /// forwarder's (behind Rancher Desktop, every client arrives from its gateway).
    /// </summary>
    public List<string> SharedAddresses { get; set; } = [];

    /// <summary>Kestrel's cap on open connections, a backstop under the limits above. Startup only.</summary>
    public int MaxConnections { get; set; } = 200;

    /// <summary>How often expired join requests and queue entries are swept.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most queued (unconfirmed) message bytes for one recipient. Files have their own limits, so normal
    /// messages always get through; this only stops one offline machine's queue from growing without end.
    /// </summary>
    public long MaxQueuedBytesPerRecipient { get; set; } = 100L * 1024 * 1024;

    /// <summary>How long the traffic log (when it's on) keeps its entries.</summary>
    public TimeSpan TrafficLogRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>A room in which nobody has said or done anything for this long collapses.</summary>
    public TimeSpan RoomIdleExpiry { get; set; } = TimeSpan.FromDays(14);

    /// <summary>How long a collapsed room's statistics are kept.</summary>
    public TimeSpan RoomStatsRetention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>How long a job claim lasts unless the claimant's daemon renews it; then the job is ready again.</summary>
    public TimeSpan JobLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a finished (done or failed) job stays on the board.</summary>
    public TimeSpan JobRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>The most unfinished jobs one machine may have posted.</summary>
    public int MaxLiveJobsPerMachine { get; set; } = 1000;
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

        if (options.JoinRequestExpiry <= TimeSpan.Zero || options.AuthenticationTimeout <= TimeSpan.Zero || options.MaintenanceInterval <= TimeSpan.Zero)
        {
            errors.Add("PoniExp:JoinRequestExpiry, AuthenticationTimeout and MaintenanceInterval must be positive.");
        }

        if (options.JoinRequestsPerHour < 1 || options.JoinRequestsPerHourPerAddress < 1 || options.MaxUnauthenticatedConnections < 1
            || options.MaxUnauthenticatedPerAddress < 1 || options.MaxConnectionsPerMachine < 1 || options.MaxConnections < 1)
        {
            errors.Add("PoniExp: the join and connection limits must be at least 1.");
        }

        if (options.JoinRequestsPerHourPerAddress > options.JoinRequestsPerHour || options.MaxUnauthenticatedPerAddress > options.MaxUnauthenticatedConnections)
        {
            errors.Add("PoniExp: a per-address limit can't be higher than the router-wide one.");
        }

        if (options.MaxQueuedBytesPerRecipient < 4L * 1024 * 1024)
        {
            errors.Add("PoniExp:MaxQueuedBytesPerRecipient must be at least 4 MB (a few full-size messages).");
        }

        if (options.TrafficLogRetention <= TimeSpan.Zero)
        {
            errors.Add("PoniExp:TrafficLogRetention must be positive.");
        }

        if (options.JobLease <= TimeSpan.Zero || options.JobRetention <= TimeSpan.Zero || options.MaxLiveJobsPerMachine < 1)
        {
            errors.Add("PoniExp:JobLease and JobRetention must be positive, and MaxLiveJobsPerMachine at least 1.");
        }

        if (options.RoomIdleExpiry <= TimeSpan.Zero || options.RoomStatsRetention <= TimeSpan.Zero)
        {
            errors.Add("PoniExp:RoomIdleExpiry and RoomStatsRetention must be positive.");
        }

        if (options.SharedAddresses.Any(a => !System.Net.IPAddress.TryParse(a, out _)))
        {
            errors.Add("PoniExp:SharedAddresses must be IP addresses.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
