using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PoniExp.Client;

/// <summary>
/// How this machine dates the messages it sends and judges the ones it receives. Bound from the
/// "Messaging" section of ~/.poniexp/settings.json (or PONIEXP_Messaging__* env vars) and read through
/// <see cref="IOptionsMonitor{T}"/>, so an edit applies without restarting a listener.
/// </summary>
public sealed class MessagingOptions
{
    public const string Section = "Messaging";

    /// <summary>How long a message lives when the sender doesn't say.</summary>
    public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The longest a message may live. Outgoing: a longer request is cut to this. Incoming: a message is
    /// treated as expiring at most this long after its <c>createdOn</c>, whatever its <c>expiresOn</c> says,
    /// which is what bounds how long this machine must remember message ids to spot replays.
    /// </summary>
    public TimeSpan MaxExpiry { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How far another machine's clock may be from this one's.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class MessagingOptionsValidator : IValidateOptions<MessagingOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingOptions options)
    {
        var errors = new List<string>();
        if (options.MaxExpiry <= TimeSpan.Zero)
        {
            errors.Add("Messaging:MaxExpiry must be positive.");
        }

        if (options.DefaultExpiry <= TimeSpan.Zero || options.DefaultExpiry > options.MaxExpiry)
        {
            errors.Add("Messaging:DefaultExpiry must be positive and at most MaxExpiry.");
        }

        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew >= options.MaxExpiry)
        {
            errors.Add("Messaging:ClockSkew must be zero or more, and less than MaxExpiry.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public static class MessagingSettings
{
    /// <summary>
    /// A validated, live <see cref="IOptionsMonitor{T}"/> over <paramref name="configuration"/>'s "Messaging"
    /// section. Reading <c>CurrentValue</c> throws <see cref="OptionsValidationException"/> for bad settings.
    /// </summary>
    public static IOptionsMonitor<MessagingOptions> Monitor(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddOptions<MessagingOptions>().Bind(configuration.GetSection(MessagingOptions.Section));
        services.AddSingleton<IValidateOptions<MessagingOptions>, MessagingOptionsValidator>();
        return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<MessagingOptions>>();
    }
}
