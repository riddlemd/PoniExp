using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoniExp.Core.Protocol;
using PoniExp.Server.Security;

namespace PoniExp.Server.Admin;

/// <summary>
/// `poniexp admin ...`: talks to a running router's admin listener on 127.0.0.1. In the container:
/// <c>docker exec poniexp-server poniexp admin list</c>.
/// </summary>
public static class AdminCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <param name="input">What <c>restore -</c> reads (default: stdin); a remote command carries it.</param>
    /// <param name="signedToken">Set when the router runs an owner-signed command (phase 11): changes go straight through.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, string? input = null, string? signedToken = null)
    {
        var url = Environment.GetEnvironmentVariable("PONIEXP_ADMIN_URL") ?? "http://127.0.0.1:5081";
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length)
            {
                url = args[++i];
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        if (signedToken is not null)
        {
            http.DefaultRequestHeaders.Add(Owner.AdminApprovals.TokenHeader, signedToken);
        }

        try
        {
            if (signedToken is null && IsChange(rest) && !(rest is ["owner", "set", ..] && rest.Contains("--lost-key")))
            {
                if (rest is ["restore", var from])
                {
                    input = from == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(from);
                    rest[1] = "-";
                }

                if (rest is ["revoke", var who] && !(who.Length == 32 && who.All(Uri.IsHexDigit)))
                {
                    // the owner signs the full machine id, which daemons check
                    var all = await http.GetFromJsonAsync<List<AdminMachineView>>("admin/machines", Json) ?? [];
                    var found = all.Where(m => m.Name == who || (who.Length >= 4 && m.Id.StartsWith(who, StringComparison.Ordinal))).ToList();
                    if (found is not [var one])
                    {
                        output.WriteLine($"error: {(found.Count == 0 ? $"no machine matches '{who}'" : $"'{who}' matches {found.Count} machines; use the id")}");
                        return 1;
                    }

                    rest[1] = one.Id;
                }

                if (await ThroughTheDeskAsync(http, rest, input, output) is { } exitCode)
                {
                    return exitCode;
                }
            }

            switch (rest)
            {
                case ["list"] or []:
                    var machines = await http.GetFromJsonAsync<List<AdminMachineView>>("admin/machines", Json) ?? [];
                    WriteTable(output, machines);
                    return 0;
                case [var verb and ("approve" or "reject" or "remove" or "revoke"), var selector]:
                    var response = await http.PostAsync($"admin/machines/{Uri.EscapeDataString(selector)}/{verb}", null);
                    if (!response.IsSuccessStatusCode)
                    {
                        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
                        output.WriteLine($"error: {(problem.TryGetProperty("detail", out var d) ? d.GetString() : response.StatusCode.ToString())}");
                        return 1;
                    }

                    var machine = (await response.Content.ReadFromJsonAsync<AdminMachineView>(Json))!;
                    output.WriteLine(verb == "revoke"
                        ? $"revoked {machine.Name} ({machine.Id[..8]}): its key is refused for good, and every machine is told"
                        : $"{verb}d {machine.Name} ({machine.Id[..8]}), join code {machine.JoinCode}: now {machine.Status}");
                    return 0;
                case ["traffic-log", var state and ("on" or "off")]:
                    var set = await ReadAsync<TrafficLogStatus>(await http.PostAsync($"admin/traffic-log/{state}", null), output);
                    output.WriteLine(set is null ? "error" : $"traffic log {(set.Enabled ? "on: the router records what it can see of each message, and refusals" : "off")}");
                    return set is null ? 1 : 0;
                case ["traffic-log"] or ["traffic-log", "status"]:
                    var status = await http.GetFromJsonAsync<TrafficLogStatus>("admin/traffic-log/status", Json);
                    output.WriteLine($"traffic log {(status!.Enabled ? "on" : "off")}");
                    return 0;
                case ["traffic-log", "show", .. var options]:
                    var query = new List<string>();
                    var json = false;
                    for (var i = 0; i < options.Count; i++)
                    {
                        switch (options[i])
                        {
                            case "--since" when i + 1 < options.Count:
                                var value = options[++i];
                                var since = DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.AssumeUniversal, out var on) ? on : DateTimeOffset.UtcNow - Duration(value);
                                query.Add("since=" + Uri.EscapeDataString(since.ToString("O")));
                                break;
                            case "--machine" when i + 1 < options.Count: query.Add("machine=" + Uri.EscapeDataString(options[++i])); break;
                            case "--limit" when i + 1 < options.Count: query.Add("limit=" + int.Parse(options[++i], System.Globalization.CultureInfo.InvariantCulture)); break;
                            case "--json": json = true; break;
                            default: throw new ArgumentException($"unknown option {options[i]}");
                        }
                    }

                    var entries = await http.GetFromJsonAsync<List<AdminTrafficEntry>>("admin/traffic-log" + (query.Count == 0 ? "" : "?" + string.Join('&', query)), Json) ?? [];
                    foreach (var t in entries)
                    {
                        output.WriteLine(json ? JsonSerializer.Serialize(t, Json)
                            : $"{t.OccurredOn:yyyy-MM-dd HH:mm:ss}Z {t.Event,-14} {t.From ?? "-",-14} {t.To ?? "-",-14} " +
                              $"{(t.MessageId is { } id ? id.ToString()[..8] : "-"),-8} {(t.Size is { } s ? $"{s}B" : ""),-8} {(t.Sequence is { } q ? $"#{q}" : ""),-6} " +
                              $"{t.Address} {t.Detail}".TrimEnd());
                    }

                    return 0;
                case ["job", "remove", var jobId]:
                    var gone = await ReadAsync<Job>(await http.PostAsync($"admin/jobs/{Uri.EscapeDataString(jobId)}/remove", null), output);
                    output.WriteLine(gone is null ? "" : $"removed job {gone.Id.ToString()[..8]} ({gone.Title})");
                    return gone is null ? 1 : 0;
                case ["jobs", .. var jobArgs]:
                    var board = await http.GetFromJsonAsync<List<Job>>("admin/jobs" + (jobArgs.Contains("--all") ? "?all=true" : ""), Json) ?? [];
                    output.WriteLine(board.Count == 0 ? "the board is empty" : string.Join("\n", board.Select(j =>
                        $"{j.Id.ToString()[..8]}  {WireProtocol.NameOf(j.Column),-8} {j.Title}" + (j.Requirements.Count > 0 ? $"  [{string.Join(", ", j.Requirements)}]" : "") +
                        $"\n    posted by {j.Poster} {j.PostedOn.ToLocalTime():yyyy-MM-dd HH:mm}" + (j.Claimant is { } who ? $", claimed by {who}" : ""))));
                    return 0;
                case ["rooms"]:
                    var live = await http.GetFromJsonAsync<List<RoomSummary>>("admin/rooms", Json) ?? [];
                    output.WriteLine(live.Count == 0 ? "no live rooms" : string.Join("\n", live.Select(r =>
                        $"{r.Name,-20} {r.RoomId.ToString()[..8]}  opened {r.OpenedOn.ToLocalTime():yyyy-MM-dd HH:mm}, last activity {r.LastActivityOn.ToLocalTime():yyyy-MM-dd HH:mm}, {Count(r.Messages, "message")}\n" +
                        $"    members: {string.Join(", ", r.Members)}" + (r.Invited.Count > 0 ? $"\n    invited: {string.Join(", ", r.Invited)}" : ""))));
                    return 0;
                case ["rooms", "--collapsed", .. var roomArgs]:
                    var collapsedSince = roomArgs is ["--since", var from] ? "?since=" + Uri.EscapeDataString(DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture).ToString("O")) : "";
                    var collapsed = await http.GetFromJsonAsync<List<RoomStats>>("admin/rooms/collapsed" + collapsedSince, Json) ?? [];
                    output.WriteLine(collapsed.Count == 0 ? "no collapsed rooms" : string.Join("\n", collapsed.Select(r =>
                        $"{r.Name,-20} {r.RoomId.ToString()[..8]}  {r.OpenedOn.ToLocalTime():yyyy-MM-dd HH:mm} to {r.CollapsedOn.ToLocalTime():yyyy-MM-dd HH:mm} ({r.Reason}), " +
                        $"{Count(r.Machines.Sum(m => m.Messages), "message")}, {r.Machines.Sum(m => m.Bytes)} bytes\n" +
                        string.Join("\n", r.Members.Select(m => $"    {m.Member,-30} {m.JoinedOn.ToLocalTime():yyyy-MM-dd HH:mm} to {m.LeftOn?.ToLocalTime():yyyy-MM-dd HH:mm}")) + "\n" +
                        string.Join("\n", r.Machines.Select(m => $"    from {m.Machine,-25} {Count(m.Messages, "message")}, {m.Bytes} bytes")))));
                    return 0;
                case ["owner"]:
                    var owner = await http.GetFromJsonAsync<AdminOwnerView>("admin/owner", Json);
                    output.WriteLine(owner?.Key is null ? "no owner key (poniexp admin owner set \"<ssh public key>\")" : ShowOwner(owner));
                    return 0;
                case ["owner", "set", .. var keyParts] when keyParts.Count > 0:
                    var lostKey = keyParts.Remove("--lost-key");
                    var setOwner = await ReadAsync<AdminOwnerView>(await http.PutAsJsonAsync("admin/owner", new AdminOwnerKey(string.Join(" ", keyParts), lostKey), Json), output);
                    if (lostKey && setOwner is not null)
                    {
                        output.WriteLine("Machines keep the key they pinned (no signed handover): run `poniexp-client owner pin` on each.");
                    }

                    output.WriteLine(setOwner is null ? "" : ShowOwner(setOwner));
                    return setOwner is null ? 1 : 0;
                case ["groups"]:
                    var groups = await http.GetFromJsonAsync<List<GroupInfo>>("admin/groups", Json) ?? [];
                    var present = (await http.GetFromJsonAsync<List<AdminMachineView>>("admin/machines", Json) ?? [])
                        .Where(m => m.Status == MembershipStatus.Approved).Select(m => m.Name).ToHashSet();
                    output.WriteLine(groups.Count == 0 ? "no groups" : string.Join("\n", groups.Select(g =>
                        $"{g.Name,-20} {string.Join(" ", g.Members.Select(m => present.Contains(m.Split('/')[0]) ? m : $"{m} (gone)"))}" +
                        (g.Signed is null ? "  [unsigned: machines with a pinned owner key ignore it]" : ""))));
                    return 0;
                case ["group", "set", var group, .. var members] when members.Count > 0:
                    var setGroup = await ReadAsync<GroupInfo>(await http.PutAsJsonAsync($"admin/groups/{Uri.EscapeDataString(group)}",
                        new GroupInfo(group, members), Json), output);
                    output.WriteLine(setGroup is null ? "" : $"group {setGroup.Name}: {string.Join(" ", setGroup.Members)}");
                    return setGroup is null ? 1 : 0;
                case ["group", "remove", var group]:
                    var removed = await ReadAsync<GroupInfo>(await http.PutAsJsonAsync($"admin/groups/{Uri.EscapeDataString(group)}",
                        new GroupInfo(group, []), Json), output);
                    output.WriteLine(removed is null ? "" : $"removed group {group}");
                    return removed is null ? 1 : 0;
                case ["blobs"]:
                    var all = await http.GetFromJsonAsync<List<AdminBlobView>>("admin/blobs", Json) ?? [];
                    if (all.Count == 0)
                    {
                        output.WriteLine("no blobs");
                    }

                    foreach (var b in all)
                    {
                        output.WriteLine($"{b.Id.ToString()[..8]}  {b.State,-10} {Blobs.BlobRegistry.Gb(b.Bytes),9}  from {b.Sender,-14} to {string.Join(",", b.SharedWith)}" +
                                         $"{(b.ReleasedBy.Count > 0 ? $" (released: {string.Join(",", b.ReleasedBy)})" : "")}  until {b.ExpiresOn:yyyy-MM-dd HH:mm}" +
                                         $"{(b.EndReason is null ? "" : $"  {b.EndReason}")}");
                    }

                    return 0;
                case ["blobs", "clear", "--for", var target]:
                    var clearResponse = await http.PostAsync($"admin/blobs/clear/{Uri.EscapeDataString(target)}", null);
                    var clearedBlobs = await ReadAsync<BlobsCleared>(clearResponse, output);
                    if (clearedBlobs is null)
                    {
                        return 1;
                    }

                    output.WriteLine($"released {clearedBlobs.Released} blob(s) waiting for {target}");
                    return 0;
                case ["clear-pending"]:
                    var cleared = await ReadAsync<List<AdminMachineView>>(await http.PostAsync("admin/clear-pending", null), output);
                    if (cleared is null)
                    {
                        return 1;
                    }

                    output.WriteLine(cleared.Count == 0 ? "no pending join requests" : $"cleared {cleared.Count} pending join request(s): " +
                        string.Join(", ", cleared.Select(m => $"{m.Name} ({m.Id[..8]})")));
                    return 0;
                case ["backup", var path]:
                    // Raw JSON, untouched: the restore must see exactly what the router wrote.
                    var backup = await http.GetStringAsync("admin/backup");
                    if (path == "-")
                    {
                        output.Write(backup);
                    }
                    else
                    {
                        PoniExp.Core.Crypto.PrivateFiles.WriteAllText(path, backup);
                        output.WriteLine($"wrote {path} (owner-only): it holds the router's private key; keep it somewhere safe");
                    }

                    return 0;
                case ["restore", var path]:
                    var body = path == "-" ? input ?? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(path);
                    var restored = await ReadAsync<RestoreResult>(await http.PostAsync("admin/restore",
                        new StringContent(body, System.Text.Encoding.UTF8, "application/json")), output);
                    if (restored is null)
                    {
                        return 1;
                    }

                    output.WriteLine($"restored {restored.Machines} approved machine(s) and {Count(restored.Jobs, "live job")}" + (restored.CertificateChanged
                        ? $"; the certificate ({restored.CertificateSha256?[..16]}...) is served after a restart: docker restart poniexp-server"
                        : "; the certificate is the one already in use") + (restored.RouterKeysChanged
                        ? "; the router's keys differ from the ones in use and are used after a restart (machines that pinned the current ones will refuse them)" : ""));
                    return 0;
                case ["certificate"]:
                    var certificate = await http.GetFromJsonAsync<RouterCertificateInfo>("admin/certificate", Json);
                    output.WriteLine(certificate?.Sha256 ?? "(the relay is running without TLS)");
                    return 0;
                default:
                    output.WriteLine("usage: poniexp admin [list | approve <id|name> | reject <id|name> | remove <id|name> | clear-pending | certificate\n" +
                                     "                      | backup <path|-> | restore <path|-> | blobs [clear --for <machine>]\n" +
                                     "                      | rooms [--collapsed [--since <time>]] | jobs [--all] | job remove <id> | revoke <machine>\n" +
        "         | owner | owner set <ssh public key>\n" +
                                     "                      | groups | group set <name> <machine/agent>... | group remove <name>\n" +
                                     "                      | traffic-log [on | off | status | show [--since 2h|date] [--machine name] [--limit n] [--json]]] [--url URL]");
                    return 2;
            }
        }
        catch (HttpRequestException e)
        {
            output.WriteLine($"error: can't reach the router's admin listener at {url} ({e.Message})");
            return 1;
        }
    }

    private static TimeSpan Duration(string text)
    {
        var unit = text[^1];
        var amount = double.Parse(text[..^1], System.Globalization.CultureInfo.InvariantCulture);
        return unit switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => throw new ArgumentException($"'{text}' isn't a duration like 30m, 2h or 3d"),
        };
    }

    /// <summary>Admin commands that change something: once there's an owner key, they run only signed.</summary>
    internal static bool IsChange(IReadOnlyList<string> args) => args switch
    {
        ["approve" or "reject" or "remove" or "revoke", _] => true,
        ["traffic-log", "on" or "off"] => true,
        ["job", "remove", _] or ["group", "set" or "remove", ..] or ["owner", "set", ..] or ["blobs", "clear", ..] => true,
        ["clear-pending"] or ["restore", _] => true,
        _ => false,
    };

    /// <summary>
    /// A docker exec change once the router has an owner key: it waits on the desk, which signs it. Null if the router
    /// has no owner key (run it directly).
    /// </summary>
    private static async Task<int?> ThroughTheDeskAsync(HttpClient http, List<string> args, string? input, TextWriter output)
    {
        var asked = await http.PostAsJsonAsync("admin/pending-commands", new AdminPendingRequest(args, input), Json);
        asked.EnsureSuccessStatusCode();
        var view = (await asked.Content.ReadFromJsonAsync<AdminPendingView>(Json))!;
        if (view.Request is not { } request)
        {
            return null;
        }

        var owner = await http.GetFromJsonAsync<AdminOwnerView>("admin/owner", Json);
        output.WriteLine($"This change needs the owner's signature. On your desk run `poniexp-client approvals` and approve the one with code {request.Code}");
        if (owner?.Desk is null)
        {
            output.WriteLine("There's no desk right now: run `poniexp-client here` on the machine you're at.");
        }

        output.WriteLine($"(waiting until {request.ExpiresOn.ToLocalTime():HH:mm:ss}; Ctrl+C stops waiting, and it times out on its own)...");
        output.Flush();
        while (true)
        {
            var state = await http.GetFromJsonAsync<AdminPendingView>($"admin/pending-commands/{request.Id}?wait=25", Json);
            switch (state?.State)
            {
                case "pending":
                    continue;
                case "approved" when state.Result is { } result:
                    output.Write(result.Output);
                    return result.ExitCode;
                case "denied":
                    output.WriteLine("denied on the desk");
                    return 1;
                default:
                    output.WriteLine("timed out: nobody approved it on the desk");
                    return 1;
            }
        }
    }

    private static string ShowOwner(AdminOwnerView o) =>
        $"owner {o.Fingerprint} {o.Key}\ndesk  {(o.Desk is null ? "none (away)" : $"{o.Desk} until {o.DeskUntil:u}")}";

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, TextWriter output) where T : class
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(Json);
        }

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        output.WriteLine($"error: {(problem.TryGetProperty("detail", out var d) ? d.GetString() : response.StatusCode.ToString())}");
        return null;
    }

    /// <summary>`poniexp health`: the Docker health check. Exit 0 if the router answers healthy on its admin listener.</summary>
    public static async Task<int> HealthAsync(TextWriter output)
    {
        var url = Environment.GetEnvironmentVariable("PONIEXP_ADMIN_URL") ?? "http://127.0.0.1:5081";
        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync("health");
            output.WriteLine(await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            output.WriteLine($"unhealthy: {e.Message}");
            return 1;
        }
    }

    private static void WriteTable(TextWriter output, IReadOnlyList<AdminMachineView> machines)
    {
        if (machines.Count == 0)
        {
            output.WriteLine("no machines yet");
            return;
        }

        output.WriteLine($"{"STATUS",-9} {"NAME",-16} {"ID",-10} {"JOIN CODE",-30} {"ONLINE",-7} DESCRIPTION");
        foreach (var m in machines)
        {
            var online = m.Status == MembershipStatus.Approved ? (m.Online ? "yes" : "no") : "-";
            output.WriteLine($"{m.Status,-9} {m.Name,-16} {m.Id[..8],-10} {m.JoinCode,-30} {online,-7} " +
                             $"{m.Description.HostName}: {m.Description.OperatingSystem} {m.Description.Architecture}" +
                             (string.IsNullOrEmpty(m.Description.Notes) ? string.Empty : $" ({m.Description.Notes})") +
                             (m.Capabilities is { } c ? $"\n          {c.Summary()}" : ""));
        }
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}

internal sealed record BlobsCleared(int Released);
