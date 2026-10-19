using Microsoft.Extensions.Options;

namespace PoniExp.Server.Blobs;

/// <summary>
/// File limits and lifetimes: the "PoniExp:Blobs" section, read live. Bytes are encrypted bytes on the
/// router. <see cref="Overrides"/> changes a machine's own limits, by machine name.
/// </summary>
public sealed class BlobOptions
{
    public const string Section = "PoniExp:Blobs";

    public long MaxFileBytes { get; set; } = 2L << 30;

    /// <summary>What one machine may have uploaded and not yet deleted. A blob shared with several machines counts once.</summary>
    public long PerSenderBytes { get; set; } = 10L << 30;

    /// <summary>What may wait for one machine. A blob counts in full against each machine it is shared with, until that machine releases it.</summary>
    public long PerRecipientBytes { get; set; } = 5L << 30;

    public long RelayBytes { get; set; } = 50L << 30;

    /// <summary>An upload is refused if it would leave less than this free on the data volume.</summary>
    public long FreeDiskFloorBytes { get; set; } = 5L << 30;

    /// <summary>A sealed blob is deleted this long after it was sealed, released or not.</summary>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromDays(7);

    /// <summary>An upload that wasn't completed is dropped after this.</summary>
    public TimeSpan UnsealedExpiry { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public Dictionary<string, MachineBlobLimits> Overrides { get; set; } = [];

    public long SenderLimitOf(string name) => Overrides.GetValueOrDefault(name)?.PerSenderBytes ?? PerSenderBytes;

    public long RecipientLimitOf(string name) => Overrides.GetValueOrDefault(name)?.PerRecipientBytes ?? PerRecipientBytes;
}

public sealed class MachineBlobLimits
{
    public long? PerSenderBytes { get; set; }

    public long? PerRecipientBytes { get; set; }
}

public sealed class BlobOptionsValidator : IValidateOptions<BlobOptions>
{
    public ValidateOptionsResult Validate(string? name, BlobOptions o)
    {
        var errors = new List<string>();
        if (o.MaxFileBytes <= 0 || o.PerSenderBytes <= 0 || o.PerRecipientBytes <= 0 || o.RelayBytes <= 0 || o.FreeDiskFloorBytes < 0)
        {
            errors.Add("PoniExp:Blobs: the byte limits must be positive (the free-disk floor zero or more).");
        }

        if (o.MaxFileBytes > o.RelayBytes || o.PerSenderBytes > o.RelayBytes || o.PerRecipientBytes > o.RelayBytes)
        {
            errors.Add("PoniExp:Blobs: no limit can be higher than RelayBytes.");
        }

        if (o.Expiry <= TimeSpan.Zero || o.UnsealedExpiry <= TimeSpan.Zero || o.TicketLifetime <= TimeSpan.Zero)
        {
            errors.Add("PoniExp:Blobs: Expiry, UnsealedExpiry and TicketLifetime must be positive.");
        }

        if (o.Overrides.Any(p => p.Value.PerSenderBytes <= 0 || p.Value.PerRecipientBytes <= 0
                                 || p.Value.PerSenderBytes > o.RelayBytes || p.Value.PerRecipientBytes > o.RelayBytes))
        {
            errors.Add("PoniExp:Blobs:Overrides: limits must be positive and at most RelayBytes.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
