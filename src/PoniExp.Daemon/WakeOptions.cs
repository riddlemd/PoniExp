using System.Net;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// The wake hook (phase 12): the "Wake" section of settings.json. A direct message for an agent named under
/// <see cref="Agents"/> sends that agent a short nudge (no bodies), for agents that only use MCP and so can't be
/// pushed to, such as Hermes. Read through <see cref="IOptionsMonitor{T}"/>; <c>wake set</c> rewrites the file.
/// </summary>
public sealed class WakeOptions
{
    public const string Section = "Wake";

    /// <summary>How long to gather a burst of messages into one nudge.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait after a failed nudge before trying again (while its messages still wait).</summary>
    public TimeSpan Retry { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long one nudge may take to be delivered (an A2A gateway's first event, a command's exit).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wake targets by agent name.</summary>
    public Dictionary<string, WakeTarget> Agents { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Exactly one of <see cref="A2aUrl"/> or <see cref="Command"/>.</summary>
public sealed class WakeTarget
{
    /// <summary>An A2A gateway (e.g. Hermes); its token, if any, is in the secret store (<c>wake-token-&lt;agent&gt;</c>).</summary>
    public string? A2aUrl { get; set; }

    /// <summary>A program to run, with the nudge on stdin and PONIEXP_AGENT, PONIEXP_COUNT, PONIEXP_FROM set.</summary>
    public string? Command { get; set; }

    public List<string> Arguments { get; set; } = [];

    /// <summary>"a2a" or "command".</summary>
    public string Kind => A2aUrl is not null ? "a2a" : "command";

    /// <summary>Null when the target is usable, else why not.</summary>
    public string? Problem(string agent)
    {
        if (!Limits.IsValidAgent(agent) || agent is Address.DaemonAgent or RoomAddress.Agent)
        {
            return $"Wake:Agents: '{agent}' isn't an agent name ({Limits.AgentRule}).";
        }

        if ((A2aUrl is null) == (Command is null))
        {
            return $"Wake:Agents:{agent} needs exactly one of A2aUrl or Command.";
        }

        if (A2aUrl is not null)
        {
            if (!Uri.TryCreate(A2aUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                return $"Wake:Agents:{agent}:A2aUrl must be an http(s) URL.";
            }

            if (url.Scheme == "http" && !IsLoopback(url))
            {
                return $"Wake:Agents:{agent}:A2aUrl: plain http only to this machine (the token travels with it); use https.";
            }
        }
        else if (string.IsNullOrWhiteSpace(Command))
        {
            return $"Wake:Agents:{agent}:Command is empty.";
        }

        return null;
    }

    private static bool IsLoopback(Uri url) =>
        url.IsLoopback || (IPAddress.TryParse(url.DnsSafeHost, out var ip) && IPAddress.IsLoopback(ip));
}

public sealed class WakeOptionsValidator : IValidateOptions<WakeOptions>
{
    public ValidateOptionsResult Validate(string? name, WakeOptions options)
    {
        var errors = new List<string>();
        if (options.Delay < TimeSpan.Zero || options.Delay > TimeSpan.FromMinutes(10))
        {
            errors.Add("Wake:Delay must be between 0 and 10 minutes.");
        }

        if (options.Retry <= TimeSpan.Zero || options.Timeout <= TimeSpan.Zero)
        {
            errors.Add("Wake:Retry and Wake:Timeout must be positive.");
        }

        errors.AddRange(options.Agents.Select(a => a.Value.Problem(a.Key)).OfType<string>());
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
