using System.Text.Json;

namespace PoniExp.Client;

/// <summary>
/// ~/.poniexp/config.json: where the router is and which certificate to pin. Beside it: machine.key (the
/// private key, owner-only), client.db (the client database) and the optional settings.json (tunable
/// settings, e.g. the "Messaging" section). Override the directory with PONIEXP_HOME.
/// </summary>
public sealed record ClientConfig(string RouterUrl, string? CertificateSha256)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string HomeDirectory =>
        Environment.GetEnvironmentVariable("PONIEXP_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".poniexp");

    public static string KeyPath => Path.Combine(HomeDirectory, "machine.key");

    public static string DatabasePath => Path.Combine(HomeDirectory, "client.db");

    public static string SettingsPath => Path.Combine(HomeDirectory, "settings.json");

    private static string ConfigPath => Path.Combine(HomeDirectory, "config.json");

    public static ClientConfig? Load() =>
        File.Exists(ConfigPath) ? JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(ConfigPath), Json) : null;

    public void Save()
    {
        Directory.CreateDirectory(HomeDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
    }

    public RelayClientOptions ToOptions() => new() { RouterUrl = new Uri(RouterUrl), PinnedCertificateSha256 = CertificateSha256 };
}
