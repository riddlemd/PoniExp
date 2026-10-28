using System.Text.Json;

namespace PoniExp.Secrets;

/// <summary>
/// Picks the store for a home directory: the "Secrets:Store" setting (settings.json, or the
/// PONIEXP_Secrets__Store environment variable, which wins), else the platform's default.
/// </summary>
public static class SecretStores
{
    public const string File = "file";
    public const string Keychain = "keychain";
    public const string Dpapi = "dpapi";
    public const string EnvironmentVariable = "PONIEXP_Secrets__Store";

    public static ISecretStore ForHome(string homeDirectory) =>
        ForHome(homeDirectory, ConfiguredStore(homeDirectory, Environment.GetEnvironmentVariable));

    internal static ISecretStore ForHome(string homeDirectory, string? store)
    {
        switch (store?.Trim().ToLowerInvariant())
        {
            case null or "":
                return Default(homeDirectory);
            case File:
                return new FileSecretStore(homeDirectory);
            case Keychain:
                if (OperatingSystem.IsMacOS())
                {
                    return new KeychainSecretStore(homeDirectory);
                }

                throw new PlatformNotSupportedException("Secrets:Store \"keychain\" is only available on macOS.");
            case Dpapi:
                if (OperatingSystem.IsWindows())
                {
                    return new DpapiSecretStore(homeDirectory);
                }

                throw new PlatformNotSupportedException("Secrets:Store \"dpapi\" is only available on Windows.");
            default:
                throw new InvalidOperationException($"unknown Secrets:Store \"{store}\": use \"{File}\", \"{Keychain}\" (macOS) or \"{Dpapi}\" (Windows).");
        }
    }

    /// <remarks>
    /// macOS defaults to files, not the keychain: without an Apple Developer team ID on the build, the
    /// keychain ties each item to the exact build that saved it, so every update locks the daemon out.
    /// No store at all on other platforms: silently keeping a key somewhere weaker only hides the failure.
    /// </remarks>
    private static ISecretStore Default(string homeDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiSecretStore(homeDirectory);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            return new FileSecretStore(homeDirectory);
        }

        throw new PlatformNotSupportedException("PoniExp has no secret store for this platform, and won't keep keys without one.");
    }

    internal static string? ConfiguredStore(string homeDirectory, Func<string, string?> environment)
    {
        if (environment(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        var settings = Path.Combine(homeDirectory, "settings.json");
        if (!System.IO.File.Exists(settings))
        {
            return null;
        }

        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(settings),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        // Case-insensitive, as the daemon's configuration binder reads the rest of settings.json.
        return Property(json.RootElement, "Secrets") is { ValueKind: JsonValueKind.Object } secrets
            && Property(secrets, "Store") is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault()
            : null;
}
