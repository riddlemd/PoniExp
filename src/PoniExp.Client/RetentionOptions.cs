using Microsoft.Extensions.Options;

namespace PoniExp.Client;

/// <summary>
/// How long the client database keeps things: the "Retention" section of settings.json, read live by the
/// daemon. Metadata and events must outlive any message that could still be accepted (MaxExpiry plus
/// ClockSkew), or a replay could pass duplicate detection; startup refuses settings that break this.
/// </summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";

    /// <summary>After this, a message's envelope (and so its body) is dropped; its metadata stays.</summary>
    public TimeSpan Envelopes { get; set; } = TimeSpan.FromDays(90);

    /// <summary>After this, a message's metadata and events go too, and so do idle conversations.</summary>
    public TimeSpan Metadata { get; set; } = TimeSpan.FromDays(365);

    public TimeSpan Diagnostics { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Above this, the oldest envelopes are dropped first (a live message's never is).</summary>
    public long MaxDatabaseBytes { get; set; } = 1L << 30;

    /// <summary>How often the daemon applies the rules.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

public sealed class RetentionOptionsValidator(IOptionsMonitor<MessagingOptions> messaging) : IValidateOptions<RetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, RetentionOptions options)
    {
        var errors = new List<string>();
        var m = messaging.CurrentValue;
        if (options.Metadata <= m.MaxExpiry + m.ClockSkew)
        {
            errors.Add("Retention:Metadata must be longer than Messaging:MaxExpiry plus ClockSkew, or replays could pass duplicate detection.");
        }

        if (options.Envelopes <= TimeSpan.Zero || options.Envelopes > options.Metadata)
        {
            errors.Add("Retention:Envelopes must be positive and at most Retention:Metadata.");
        }

        if (options.Diagnostics <= TimeSpan.Zero)
        {
            errors.Add("Retention:Diagnostics must be positive.");
        }

        if (options.Interval <= TimeSpan.Zero)
        {
            errors.Add("Retention:Interval must be positive.");
        }

        if (options.MaxDatabaseBytes < 1 << 20)
        {
            errors.Add("Retention:MaxDatabaseBytes must be at least 1 MB.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
