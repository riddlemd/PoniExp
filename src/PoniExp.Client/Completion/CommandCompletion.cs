namespace PoniExp.Client.Completion;

/// <summary>What a word on the command line names, when the candidates come from the daemon.</summary>
public enum CompletionKind
{
    PendingApproval,
    InboxMessage,
    LoggedMessage,
    Conversation,
    Rule,
    Job,
    Room,
    LocalAgent,
    WakeAgent,
    Machine,
    Address,
    Blob,
}

/// <summary>Lists what exists for a <see cref="CompletionKind"/>. <c>agent</c> is the <c>--agent</c> already typed, or null for every agent here.</summary>
public interface ICompletionSource
{
    Task<IReadOnlyList<string>> ListAsync(CompletionKind kind, string? agent, CancellationToken ct);
}

/// <summary>
/// Tab completion for <c>poniexp-client</c>: given the words typed so far (the last one is the word under the cursor,
/// possibly empty), the candidates that word may become.
/// </summary>
public static class CommandCompletion
{
    public static readonly IReadOnlyList<string> Commands =
    [
        "ack", "admin", "agent", "approvals", "approve", "away", "blobs", "cancel", "capabilities", "completion", "conversation",
        "conversations", "daemon", "deny", "diagnostics", "doctor", "fetch", "here", "id", "inbox", "init", "job", "join", "keys",
        "listen", "log", "machines", "mcp", "message", "owner", "release", "reply", "room", "send", "service", "status", "wake",
    ];

    public static readonly IReadOnlyList<string> Shells = ["bash", "powershell", "zsh"];

    private static readonly string[] JobColumns = ["backlog", "ready", "running", "done", "failed"];

    private static readonly Dictionary<string, string[]> Subcommands = new(StringComparer.Ordinal)
    {
        ["agent"] = ["list", "retire"],
        ["approvals"] = ["rules"],
        ["completion"] = [.. Shells],
        ["job"] = ["list", "show", "post", "claim", "release", "cancel", "move", "comment"],
        ["keys"] = ["rotate", "reset"],
        ["owner"] = ["pin"],
        ["room"] = ["list", "create", "invite", "invites", "join", "leave", "read", "send"],
        ["service"] = ["install", "uninstall", "status"],
        ["wake"] = ["set", "test", "remove"],
    };

    private static readonly Dictionary<string, string[]> Options = new(StringComparer.Ordinal)
    {
        ["ack"] = ["--agent"],
        ["approvals"] = ["--mine"],
        ["approve"] = ["--always"],
        ["conversation"] = ["--agent", "--json"],
        ["conversations"] = ["--agent"],
        ["deny"] = ["--reason"],
        ["fetch"] = ["--agent", "--blob"],
        ["here"] = ["--hours"],
        ["inbox"] = ["--agent", "--json"],
        ["init"] = ["--pin"],
        ["job"] = ["--as", "--all", "--column", "--description", "--require", "--backlog", "--note"],
        ["join"] = ["--notes"],
        ["listen"] = ["--agent", "--json", "--ack", "--auto-fetch"],
        ["log"] = ["--conversation", "--operation", "--peer", "--agent", "--since", "--limit", "--automatic", "--json", "--export", "--bodies"],
        ["mcp"] = ["--agent"],
        ["message"] = ["--agent"],
        ["release"] = ["--agent", "--blob"],
        ["reply"] = ["--agent", "--status", "--meta", "--attach"],
        ["room"] = ["--as", "--agent", "--invite", "--since", "--json", "--reply"],
        ["send"] = ["--to", "--from", "--subject", "--conversation", "--operation", "--expires", "--content-type", "--meta", "--attach"],
        ["service"] = ["--system", "--dry-run"],
        ["wake"] = ["--a2a", "--command", "--token"],
    };

    // Options that take a value; the ones not listed in OptionValues take free text (nothing to complete).
    private static readonly HashSet<string> ValueOptions =
    [
        "--agent", "--as", "--from", "--to", "--invite", "--conversation", "--status", "--peer", "--column", "--blob", "--reason",
        "--hours", "--pin", "--notes", "--subject", "--operation", "--expires", "--content-type", "--meta", "--attach", "--since",
        "--limit", "--export", "--description", "--require", "--note", "--reply", "--a2a", "--command", "--home",
    ];

    private static readonly Dictionary<string, CompletionKind> OptionKinds = new(StringComparer.Ordinal)
    {
        ["--agent"] = CompletionKind.LocalAgent,
        ["--as"] = CompletionKind.LocalAgent,
        ["--from"] = CompletionKind.LocalAgent,
        ["--to"] = CompletionKind.Address,
        ["--invite"] = CompletionKind.Address,
        ["--conversation"] = CompletionKind.Conversation,
        ["--peer"] = CompletionKind.Machine,
    };

    private static readonly Dictionary<string, string[]> OptionValues = new(StringComparer.Ordinal)
    {
        ["--status"] = ["progress", "done", "failed"],
        ["--column"] = JobColumns,
    };

    /// <summary>The candidates for the last word of <paramref name="words"/> (the words after the program name).</summary>
    public static async Task<IReadOnlyList<string>> CompleteAsync(IReadOnlyList<string> words, ICompletionSource source, CancellationToken ct = default)
    {
        var typed = words.Count == 0 ? [string.Empty] : words.ToList();
        if (typed.Count >= 3 && typed[0] == "--home")
        {
            typed.RemoveRange(0, 2);
        }

        var current = typed[^1];
        var before = typed.Take(typed.Count - 1).ToList();
        var all = await CandidatesAsync(before, current, source, ct);
        return [.. all.Where(c => c.StartsWith(current, StringComparison.OrdinalIgnoreCase))];
    }

    private static async Task<IReadOnlyList<string>> CandidatesAsync(List<string> before, string current, ICompletionSource source, CancellationToken ct)
    {
        if (before.Count == 0)
        {
            return Commands;
        }

        var command = before[0];
        if (before[^1] is var last && ValueOptions.Contains(last))
        {
            return OptionKinds.TryGetValue(last, out var valueKind) ? await source.ListAsync(valueKind, AgentTyped(before), ct)
                : OptionValues.TryGetValue(last, out var fixedValues) ? fixedValues : [];
        }

        if (current.StartsWith('-'))
        {
            return Options.GetValueOrDefault(command) ?? [];
        }

        var positional = Positional(before);
        var agent = AgentTyped(before);
        Task<IReadOnlyList<string>> From(CompletionKind kind) => source.ListAsync(kind, agent, ct);
        return (command, positional) switch
        {
            ("approve" or "deny", [_]) => await From(CompletionKind.PendingApproval),
            ("ack" or "reply", [_]) => await From(CompletionKind.InboxMessage),
            ("message" or "fetch" or "release", [_]) => await From(CompletionKind.LoggedMessage),
            ("conversation", [_]) => await From(CompletionKind.Conversation),
            ("cancel", [_]) => await From(CompletionKind.Blob),
            ("approvals", [_, "rules"]) => ["remove"],
            ("approvals", [_, "rules", "remove"]) => await From(CompletionKind.Rule),
            ("job", [_, "show" or "claim" or "release" or "cancel" or "move" or "comment"]) => await From(CompletionKind.Job),
            ("job", [_, "move", _]) => JobColumns,
            ("room", [_, "invite" or "join" or "leave" or "read" or "send"]) => await From(CompletionKind.Room),
            ("room", [_, "invite", _, ..]) => await From(CompletionKind.Address),
            ("wake", [_, "test" or "remove"]) => await From(CompletionKind.WakeAgent),
            ("wake", [_, "set"]) => await From(CompletionKind.LocalAgent),
            ("agent", [_, "retire"]) => await From(CompletionKind.LocalAgent),
            (_, [_]) when Subcommands.TryGetValue(command, out var subs) => subs,
            _ => [],
        };
    }

    // The command and its positional words so far: options and their values left out.
    private static List<string> Positional(List<string> before)
    {
        var positional = new List<string>();
        for (var i = 0; i < before.Count; i++)
        {
            if (before[i].StartsWith("--", StringComparison.Ordinal))
            {
                i += ValueOptions.Contains(before[i]) ? 1 : 0;
                continue;
            }

            positional.Add(before[i]);
        }

        return positional;
    }

    private static string? AgentTyped(List<string> before)
    {
        var i = before.LastIndexOf("--agent");
        return i >= 0 && i + 1 < before.Count ? before[i + 1] : null;
    }

    /// <summary>
    /// Splits a command line up to the cursor into the words after the program name; a trailing space starts an empty
    /// word. Quotes group a word and are dropped.
    /// </summary>
    public static IReadOnlyList<string> SplitLine(string line)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        var inWord = false;
        char? quote = null;
        foreach (var c in line)
        {
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                (quote, inWord) = (c, true);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    (word, inWord) = (new System.Text.StringBuilder(), false);
                }
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        words.Add(inWord ? word.ToString() : string.Empty);
        return words.Count > 1 ? words.Skip(1).ToList() : [string.Empty];
    }

    /// <summary>
    /// Bash treats <c>:</c> and <c>=</c> as word breaks, so the word it replaces starts after the last one: drop that
    /// much of each candidate.
    /// </summary>
    public static IReadOnlyList<string> ForBash(string current, IReadOnlyList<string> candidates)
    {
        var cut = current.LastIndexOfAny([':', '=']) + 1;
        return cut == 0 ? candidates : [.. candidates.Select(c => c[cut..])];
    }
}
