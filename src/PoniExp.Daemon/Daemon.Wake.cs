using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoniExp.Client;

namespace PoniExp.Daemon;

/// <summary>
/// The wake hook (phase 12): a doorbell for agents that only use MCP, so can't be pushed to (Hermes). A direct
/// message for an agent with a wake target sends it a short nudge, never the bodies: the agent then reads its
/// inbox through MCP. One nudge per burst (<see cref="WakeOptions.Delay"/>), retried after a failure while
/// the messages it covers still wait.
/// </summary>
public sealed partial class Daemon
{
    internal static string WakeTokenName(string agent) => $"wake-token-{agent}";

    private static readonly HttpClient WakeHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim SettingsGate = new(1, 1);
    private readonly ConcurrentDictionary<string, WakeState> _wakeStates = new(StringComparer.Ordinal);

    private sealed class WakeState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Scheduled;
        public bool Started;
        public readonly HashSet<Guid> Nudged = [];
        public DateTimeOffset? LastNudge;
        public DateTimeOffset? LastFailure;
        public string? LastError;
    }

    /// <summary>A direct message was stored for <paramref name="agent"/>: nudge it after the delay, once per burst.</summary>
    private void WakeOnMessage(string agent)
    {
        var options = _wake.CurrentValue;
        if (!options.Agents.ContainsKey(agent))
        {
            return;
        }

        var state = _wakeStates.GetOrAdd(agent, _ => new WakeState());
        if (Interlocked.Exchange(ref state.Scheduled, 1) == 1)
        {
            return; // a nudge is already on its way; it will cover this message
        }

        Background(async () =>
        {
            try
            {
                await Task.Delay(options.Delay, _time, _stopping);
            }
            finally
            {
                Volatile.Write(ref state.Scheduled, 0);
            }

            await NudgeAsync(agent, state, test: false);
        });
    }

    /// <summary>From the sweep: the first nudge after start (what already waits) and retries after a failure.</summary>
    private void WakeSweep()
    {
        var options = _wake.CurrentValue;
        foreach (var agent in options.Agents.Keys)
        {
            var state = _wakeStates.GetOrAdd(agent, _ => new WakeState());
            if (Volatile.Read(ref state.Scheduled) == 1 || (state.Started && (state.LastFailure is not { } failed || Now - failed < options.Retry)))
            {
                continue;
            }

            Background(async () => await NudgeAsync(agent, state, test: false));
        }
    }

    /// <summary>Sends one nudge covering the agent's waiting messages not yet nudged about (all of them for a test).</summary>
    private async Task NudgeAsync(string agent, WakeState state, bool test)
    {
        if (!await state.Gate.WaitAsync(test ? _wake.CurrentValue.Timeout * 2 : TimeSpan.Zero))
        {
            if (test)
            {
                throw new InvalidOperationException($"a nudge for '{agent}' is already being sent");
            }

            return;
        }

        try
        {
            state.Started = true;
            var options = _wake.CurrentValue;
            if (!options.Agents.TryGetValue(agent, out var target))
            {
                throw new KeyNotFoundException($"no wake target for agent '{agent}'");
            }

            var inbox = Store.GetInbox(MachineId, agent);
            state.Nudged.IntersectWith(inbox.Select(m => m.Id)); // what was acknowledged, rejected or expired drops out
            var covered = test ? inbox : inbox.Where(m => !state.Nudged.Contains(m.Id)).ToList();
            if (covered.Count == 0 && !test)
            {
                state.LastFailure = null;
                return;
            }

            var from = covered.Select(m => $"{NameOf(m.FromMachine)}/{m.FromAgent}").Distinct().ToList();
            var text = NudgeText(agent, covered.Count, from, test);
            if (target.A2aUrl is { } url)
            {
                await NudgeA2aAsync(agent, url, text, options.Timeout);
            }
            else
            {
                await NudgeCommandAsync(agent, target, text, covered.Count, from, options.Timeout);
            }

            state.Nudged.UnionWith(covered.Select(m => m.Id));
            (state.LastNudge, state.LastFailure, state.LastError) = (Now, null, null);
            Diagnose("info", "wake", $"woke {agent} ({target.Kind}): {covered.Count} message(s){(test ? ", a test" : "")}");
        }
        catch (Exception e) when (e is not KeyNotFoundException)
        {
            (state.LastFailure, state.LastError) = (Now, e.Message);
            Diagnose("warning", "wake", $"couldn't wake {agent}: {e.Message}");
            if (test)
            {
                throw new InvalidOperationException($"couldn't wake {agent}: {e.Message}", e);
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal static string NudgeText(string agent, int count, IReadOnlyList<string> from, bool test)
    {
        const string how = "Read them with the poniexp MCP `inbox` tool, then `ack` each one you take on and answer with `reply`.";
        if (test)
        {
            return $"{SessionWake.NudgeTag} a test nudge for agent '{agent}' (from `poniexp-client wake test`). "
                + (count == 0 ? "Nothing waits in its inbox; no need to do anything." : $"{count} message(s) wait in its inbox. {how}");
        }

        return $"{SessionWake.NudgeTag} {count} new message{(count == 1 ? "" : "s")} for agent '{agent}' from {string.Join(", ", from)}. {how}";
    }

    // ---- A2A ----

    /// <summary>
    /// <c>message/stream</c> with a fresh context (so a gateway's per-context turn limit never applies). Delivered on
    /// the first non-terminal event; the rest of the stream is read to its end in the background, since closing
    /// it early would fail the task.
    /// </summary>
    private async Task NudgeA2aAsync(string agent, string url, string text, TimeSpan timeout)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = "message/stream",
            ["params"] = new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "ROLE_USER",
                    ["messageId"] = Guid.NewGuid().ToString("N"),
                    ["contextId"] = $"poniexp-wake-{Guid.NewGuid():N}",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = text, ["mediaType"] = "text/plain" }),
                },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (_context.Secrets?.Load(WakeTokenName(agent)) is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(token));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        cts.CancelAfter(timeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await WakeHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cts.Token);
                throw new InvalidOperationException($"the gateway answered {(int)response.StatusCode} {response.ReasonPhrase}{Describe(detail)}");
            }

            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                throw new InvalidOperationException($"the gateway didn't stream{Describe(await reader.ReadToEndAsync(cts.Token))}");
            }

            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("the gateway closed the stream before taking the nudge");
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var (state, detail) = ParseA2aEvent(line[5..]);
                if (state is null)
                {
                    continue;
                }

                if (state is "submitted" or "working" or "completed" or "input-required")
                {
                    var taken = response;
                    response = null;
                    _ = DrainAsync(taken, reader);
                    return;
                }

                throw new InvalidOperationException($"the gateway answered {state}{(detail is null ? "" : $": {detail}")}");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer from {url} within {timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException($"couldn't reach {url}: {e.Message}", e);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task DrainAsync(HttpResponseMessage response, StreamReader reader)
    {
        using (response)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping))
        {
            cts.CancelAfter(TimeSpan.FromHours(24)); // a gateway's own ceiling on an unanswered task
            try
            {
                while (await reader.ReadLineAsync(cts.Token) is not null)
                {
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException)
            {
            }
        }
    }

    /// <summary>The task state of one SSE event (submitted, working, completed, failed, ...) and its text, or a JSON-RPC error as "error".</summary>
    internal static (string? State, string? Detail) ParseA2aEvent(string data)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        if (node?["error"] is { } error)
        {
            return ("error", error["message"]?.GetValue<string>() ?? error.ToJsonString());
        }

        var result = node?["result"] ?? node;
        var status = result?["task"]?["status"] ?? result?["statusUpdate"]?["status"] ?? result?["status"];
        if (status?["state"]?.GetValue<string>() is not { } raw)
        {
            return (null, null);
        }

        var state = raw.ToLowerInvariant().Replace("task_state_", "", StringComparison.Ordinal).Replace('_', '-');
        var text = status["message"]?["parts"]?.AsArray().Select(p => p?["text"]?.GetValue<string>()).FirstOrDefault(t => t is not null);
        return (state, text);
    }

    private static string Describe(string body) =>
        string.IsNullOrWhiteSpace(body) ? "" : ": " + (body.Length > 200 ? body[..200] + "..." : body).Trim();

    // ---- commands ----

    private static async Task NudgeCommandAsync(string agent, WakeTarget target, string text, int count, IReadOnlyList<string> from, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(target.Command!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in target.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PONIEXP_AGENT"] = agent;
        start.Environment["PONIEXP_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PONIEXP_FROM"] = string.Join(",", from);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"couldn't start {target.Command}");
        _ = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // it didn't read stdin; its exit code still decides
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{target.Command} didn't finish within {timeout.TotalSeconds:0} s");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{target.Command} exited with {process.ExitCode}{Describe(await errors)}");
        }
    }

    // ---- setup (local API) ----

    internal IReadOnlyList<LocalWakeTarget> WakeTargets() =>
        [.. _wake.CurrentValue.Agents.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => WakeView(a.Key, a.Value))];

    private LocalWakeTarget WakeView(string agent, WakeTarget target)
    {
        var state = _wakeStates.GetValueOrDefault(agent);
        return new LocalWakeTarget(agent, target.Kind,
            target.A2aUrl ?? string.Join(' ', new[] { target.Command! }.Concat(target.Arguments)),
            target.A2aUrl is not null && _context.Secrets?.Load(WakeTokenName(agent)) is not null,
            Store.GetInbox(MachineId, agent).Count, state?.LastNudge, state?.LastFailure, state?.LastError);
    }

    internal async Task<LocalWakeTarget> TestWakeAsync(string agent)
    {
        if (!_wake.CurrentValue.Agents.TryGetValue(agent, out var target))
        {
            throw new KeyNotFoundException($"no wake target for agent '{agent}'");
        }

        await NudgeAsync(agent, _wakeStates.GetOrAdd(agent, _ => new WakeState()), test: true);
        return WakeView(agent, target);
    }

    /// <summary>
    /// Sets (or with a null <paramref name="target"/>, removes) an agent's wake target in this daemon's settings.json,
    /// and keeps its token in the secret store; returns once the settings have reloaded.
    /// </summary>
    internal async Task<LocalWakeTarget?> SetWakeTargetAsync(string agent, WakeTarget? target, string? token)
    {
        if (target?.Problem(agent) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        if (target is null && !_wake.CurrentValue.Agents.ContainsKey(agent))
        {
            throw new KeyNotFoundException($"no wake target for agent '{agent}'");
        }

        if (token is not null && target?.A2aUrl is null)
        {
            throw new ArgumentException("a token is only for an A2A target");
        }

        var secrets = _context.Secrets;
        if (token is not null && secrets is null)
        {
            throw new InvalidOperationException("this daemon has no secret store to keep the token in");
        }

        await SettingsGate.WaitAsync();
        try
        {
            var path = Path.Combine(_context.Home, "settings.json");
            var root = File.Exists(path)
                ? JsonNode.Parse(await File.ReadAllTextAsync(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    as JsonObject ?? throw new InvalidOperationException("settings.json isn't a JSON object")
                : [];
            var agents = Child(Child(root, "Wake"), "Agents");
            foreach (var key in agents.Select(p => p.Key).Where(k => string.Equals(k, agent, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                agents.Remove(key);
            }

            if (target is not null)
            {
                agents[agent] = target.A2aUrl is { } url
                    ? new JsonObject { ["A2aUrl"] = url }
                    : new JsonObject { ["Command"] = target.Command, ["Arguments"] = new JsonArray([.. target.Arguments.Select(a => (JsonNode?)a)]) };
            }

            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            SettingsGate.Release();
        }

        if (token is not null)
        {
            secrets!.Save(WakeTokenName(agent), Encoding.UTF8.GetBytes(token));
        }
        else if (target?.A2aUrl is null)
        {
            secrets?.Delete(WakeTokenName(agent));
        }

        var deadline = Now + TimeSpan.FromSeconds(10);
        while (!Applied())
        {
            if (Now > deadline)
            {
                throw new InvalidOperationException("settings.json was written, but the daemon's settings didn't change (is Wake set in its environment, PONIEXP_Wake__...?)");
            }

            await Task.Delay(100);
        }

        _wakeStates.TryRemove(agent, out _); // starts afresh: the next sweep nudges about what already waits
        return target is null ? null : WakeView(agent, _wake.CurrentValue.Agents[agent]);

        bool Applied()
        {
            var current = _wake.CurrentValue.Agents.GetValueOrDefault(agent);
            return target is null ? current is null
                : current is not null && current.A2aUrl == target.A2aUrl && current.Command == target.Command && current.Arguments.SequenceEqual(target.Arguments);
        }
    }

    private static JsonObject Child(JsonObject parent, string name)
    {
        var key = parent.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (key is not null && parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key ?? name] = created;
        return created;
    }
}
