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
                case ["certificate"]:
                    var certificate = await http.GetFromJsonAsync<RouterCertificateInfo>("admin/certificate", Json);
                    output.WriteLine(certificate?.Sha256 ?? "(the relay is running without TLS)");
                    return 0;
                default:
                    output.WriteLine("usage: poniexp admin [list | approve <id|name> | reject <id|name> | remove <id|name> | certificate] [--url URL]");
                    return 2;
            }
        }
        catch (HttpRequestException e)
        {
            output.WriteLine($"error: can't reach the router's admin listener at {url} ({e.Message})");
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
