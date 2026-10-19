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

    public static async Task<int> RunAsync(string[] args, TextWriter output)
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

        using var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
        try
        {
            switch (rest)
            {
                case ["list"] or []:
                    var machines = await http.GetFromJsonAsync<List<AdminMachineView>>("admin/machines", Json) ?? [];
                    WriteTable(output, machines);
                    return 0;
                case [var verb and ("approve" or "reject" or "remove"), var selector]:
                    var response = await http.PostAsync($"admin/machines/{Uri.EscapeDataString(selector)}/{verb}", null);
                    if (!response.IsSuccessStatusCode)
                    {
                        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
                        output.WriteLine($"error: {(problem.TryGetProperty("detail", out var d) ? d.GetString() : response.StatusCode.ToString())}");
                        return 1;
                    }

                    var machine = (await response.Content.ReadFromJsonAsync<AdminMachineView>(Json))!;
                    output.WriteLine($"{verb}d {machine.Name} ({machine.Id[..8]}), join code {machine.JoinCode}: now {machine.Status}");
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
                case ["groups"]:
                    var groups = await http.GetFromJsonAsync<List<GroupInfo>>("admin/groups", Json) ?? [];
                    output.WriteLine(groups.Count == 0 ? "no groups" : string.Join("\n", groups.Select(g => $"{g.Name,-20} {string.Join(" ", g.Members)}")));
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
                    var body = path == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(path);
                    var restored = await ReadAsync<RestoreResult>(await http.PostAsync("admin/restore",
                        new StringContent(body, System.Text.Encoding.UTF8, "application/json")), output);
                    if (restored is null)
                    {
                        return 1;
                    }

                    output.WriteLine($"restored {restored.Machines} approved machine(s)" + (restored.CertificateChanged
                        ? $"; the certificate ({restored.CertificateSha256?[..16]}...) is served after a restart: docker restart poniexp-server"
                        : "; the certificate is the one already in use"));
                    return 0;
                case ["certificate"]:
                    var certificate = await http.GetFromJsonAsync<RouterCertificateInfo>("admin/certificate", Json);
                    output.WriteLine(certificate?.Sha256 ?? "(the relay is running without TLS)");
                    return 0;
                default:
                    output.WriteLine("usage: poniexp admin [list | approve <id|name> | reject <id|name> | remove <id|name> | clear-pending | certificate\n" +
                                     "                      | backup <path|-> | restore <path|-> | blobs [clear --for <machine>]\n" +
                                     "                      | rooms [--collapsed [--since <time>]]\n" +
                                     "                      | groups | group set <name> <machine/agent|role:x[/all]>... | group remove <name>\n" +
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
                             (string.IsNullOrEmpty(m.Description.Notes) ? string.Empty : $" ({m.Description.Notes})"));
        }
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}

internal sealed record BlobsCleared(int Released);
