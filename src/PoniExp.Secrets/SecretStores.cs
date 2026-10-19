namespace PoniExp.Secrets;

/// <summary>Picks the store for the platform this is running on.</summary>
public static class SecretStores
{
    /// <remarks>No store otherwise: silently keeping a key somewhere weaker only hides the failure.</remarks>
    public static ISecretStore ForHome(string homeDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiSecretStore(homeDirectory);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new KeychainSecretStore(homeDirectory);
        }

        if (OperatingSystem.IsLinux())
        {
            return new FileSecretStore(homeDirectory);
        }

        throw new PlatformNotSupportedException("PoniExp has no secret store for this platform, and won't keep keys without one.");
    }
}
