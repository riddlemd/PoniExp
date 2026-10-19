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
                                     "                      | backup <path|-> | restore <path|->] [--url URL]");
                    return 2;
            }
        }
        catch (HttpRequestException e)
        {
            output.WriteLine($"error: can't reach the router's admin listener at {url} ({e.Message})");
            return 1;
        }
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
}
