using PoniExp.Secrets;

namespace PoniExp.Tests;

/// <summary>
/// Which secret store a home directory uses: the Secrets:Store setting (environment first, then
/// settings.json), else the platform default (DPAPI on Windows, owner-only files on macOS and Linux).
/// And what the daemon does when its store refuses to hand over the keys.
/// </summary>
public class SecretStoreSelectionTests
{
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    // ---- the platform default ----

    [Fact]
    public void Given_NoSetting_When_PickingTheStore_Then_ItIsDpapiOnWindowsAndOwnerOnlyFilesElsewhere()
    {
        // Given
        var home = TempHome();

        // When
        var store = SecretStores.ForHome(home, SecretStores.ConfiguredStore(home, NoEnvironment));

        // Then
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<DpapiSecretStore>(store);
        }
        else
        {
            Assert.IsType<FileSecretStore>(store);
        }
    }

    // ---- the setting ----

    [Fact]
    public void Given_SettingsJsonNamesTheKeychain_When_PickingTheStore_Then_ItIsTheKeychainOnMacOSAndRefusedElsewhere()
    {
        // Given
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "settings.json"), """{ "secrets": { "store": "Keychain" } }""");

        // When
        var configured = SecretStores.ConfiguredStore(home, NoEnvironment);

        // Then
        if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<KeychainSecretStore>(SecretStores.ForHome(home, configured));
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() => SecretStores.ForHome(home, configured));
        }
    }

    [Fact]
    public void Given_SettingsJsonNamesFiles_When_PickingTheStore_Then_ItIsOwnerOnlyFilesOnEveryPlatform()
    {
        // Given
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "settings.json"), """{ "Daemon": { "AgentTimeout": "00:01:00" }, "Secrets": { "Store": "file" } }""");

        // When
        var store = SecretStores.ForHome(home, SecretStores.ConfiguredStore(home, NoEnvironment));

        // Then
        Assert.IsType<FileSecretStore>(store);
    }

    [Fact]
    public void Given_TheEnvironmentAndSettingsJsonDisagree_When_ReadingTheSetting_Then_TheEnvironmentWins()
    {
        // Given
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "settings.json"), """{ "Secrets": { "Store": "keychain" } }""");
        Func<string, string?> environment = name => name == SecretStores.EnvironmentVariable ? "file" : null;

        // When
        var configured = SecretStores.ConfiguredStore(home, environment);

        // Then
        Assert.Equal("file", configured);
    }

    [Fact]
    public void Given_SettingsJsonWithoutASecretsSection_When_ReadingTheSetting_Then_ThereIsNone()
    {
        // Given
        var home = TempHome();
        File.WriteAllText(Path.Combine(home, "settings.json"), """{ "Messaging": { "DefaultExpiry": "1.00:00:00" } }""");

        // When
        var configured = SecretStores.ConfiguredStore(home, NoEnvironment);

        // Then
        Assert.Null(configured);
    }

    [Fact]
    public void Given_AnUnknownStoreName_When_PickingTheStore_Then_ItFailsNamingTheChoices()
    {
        // Given
        var home = TempHome();

        // When
        var error = Assert.Throws<InvalidOperationException>(() => SecretStores.ForHome(home, "vault"));

        // Then
        Assert.Contains("\"vault\"", error.Message);
        Assert.Contains("\"keychain\"", error.Message);
    }

    // ---- a store that refuses ----

    [Fact]
    public async Task Given_AStoreThatRefusesToLoad_When_TheDaemonLoadsItsKeys_Then_ItReportsItAndWaitsUntilStopped()
    {
        // Given
        var home = TempHome();
        var error = new StringWriter();
        using var stop = new CancellationTokenSource();

        // When
        var loading = KeyRing.LoadOrWaitAsync(home, new RefusingStore(), error, stop.Token);
        await Task.Delay(300);
        var finishedBeforeStop = loading.IsCompleted;
        await stop.CancelAsync();
        var keys = await loading.WaitAsync(TimeSpan.FromSeconds(5));

        // Then
        Assert.False(finishedBeforeStop);
        Assert.Null(keys);
        Assert.Contains("a store that refuses", error.ToString());
        Assert.Contains("user denied access", error.ToString());
    }

    [Fact]
    public async Task Given_AWorkingStore_When_TheDaemonLoadsItsKeys_Then_ItGetsThemAtOnce()
    {
        // Given
        var home = TempHome();
        var store = new FileSecretStore(home);
        using var expected = KeyRing.LoadOrCreate(home, store);

        // When
        using var keys = await KeyRing.LoadOrWaitAsync(home, store, TextWriter.Null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Then
        Assert.Equal(expected.MachineId, keys!.MachineId);
    }

    private sealed class RefusingStore : ISecretStore
    {
        public string Description => "a store that refuses";

        public byte[]? Load(string name) => throw new InvalidOperationException("user denied access");

        public void Save(string name, byte[] secret) => throw new InvalidOperationException("user denied access");

        public bool Delete(string name) => false;
    }

    private static string TempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "poniexp-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }
}
