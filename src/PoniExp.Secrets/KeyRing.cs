using System.Globalization;
using System.Text;
using System.Text.Json;
using PoniExp.Core.Crypto;

namespace PoniExp.Secrets;

/// <summary>
/// Loads a machine's two private keys from its secret store, creating them on first use. A machine.key
/// file left by an older version (an unprotected PEM) is moved into the store and deleted.
/// </summary>
public static class KeyRing
{
    public const string IdentityName = "identity";
    public const string EncryptionName = "encryption";
    public const string LegacyKeyFile = "machine.key";
    public const string EncryptionIssuedName = "encryption-issued";
    public const string RetiredName = "encryption-retired";

    public static MachineKeys LoadOrCreate(string homeDirectory, ISecretStore? store = null)
    {
        store ??= SecretStores.ForHome(homeDirectory);
        Directory.CreateDirectory(homeDirectory);

        var legacy = Path.Combine(homeDirectory, LegacyKeyFile);
        if (store.Load(IdentityName) is null && File.Exists(legacy))
        {
            store.Save(IdentityName, File.ReadAllBytes(legacy));
            File.Delete(legacy);
        }

        var identity = Load(store, IdentityName, MachineIdentity.FromPem, () => MachineIdentity.Create(), k => k.ExportPrivateKeyPem());
        var encryption = Load(store, EncryptionName, EncryptionKey.FromPem, EncryptionKey.Create, k => k.ExportPrivateKeyPem());
        if (store.Load(EncryptionIssuedName) is not { } issuedBytes)
        {
            issuedBytes = Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            store.Save(EncryptionIssuedName, issuedBytes);
        }

        var issued = DateTimeOffset.Parse(Encoding.UTF8.GetString(issuedBytes), CultureInfo.InvariantCulture);
        var retired = store.Load(RetiredName) is { } json
            ? JsonSerializer.Deserialize<List<StoredRetiredKey>>(json)!.Select(r => new RetiredEncryptionKey(EncryptionKey.FromPem(r.Pem), r.RetiredOn))
            : [];
        return new MachineKeys(identity, encryption, issued, retired);
    }

    /// <summary>
    /// <see cref="LoadOrCreate"/> for the daemon: when the store refuses (e.g. the keychain denies a build
    /// it doesn't recognise), reports it and waits for <paramref name="cancel"/> instead of failing, then
    /// returns null.
    /// </summary>
    /// <remarks>
    /// Exiting would make the service manager (launchd KeepAlive, the Run key's restarts) start the daemon
    /// again at once, and every start can raise another OS prompt.
    /// </remarks>
    public static async Task<MachineKeys?> LoadOrWaitAsync(string homeDirectory, ISecretStore store, TextWriter error, CancellationToken cancel)
    {
        try
        {
            return LoadOrCreate(homeDirectory, store);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await error.WriteLineAsync($"error: can't read this machine's keys from {store.Description}: {e.Message}");
            await error.WriteLineAsync("  not retrying, so the OS doesn't ask again: fix the store (or Secrets:Store in settings.json), then restart the daemon");
            await error.FlushAsync(CancellationToken.None);
            try
            {
                await Task.Delay(Timeout.Infinite, cancel);
            }
            catch (OperationCanceledException)
            {
            }

            return null;
        }
    }

    /// <summary>Stores the encryption keys after a rotation (or after retired ones were dropped).</summary>
    public static void SaveEncryption(string homeDirectory, MachineKeys keys, ISecretStore? store = null)
    {
        store ??= SecretStores.ForHome(homeDirectory);
        store.Save(RetiredName, JsonSerializer.SerializeToUtf8Bytes(keys.RetiredEncryptionKeys.Select(r => new StoredRetiredKey(r.Key.ExportPrivateKeyPem(), r.RetiredOn)).ToList()));
        store.Save(EncryptionName, Encoding.UTF8.GetBytes(keys.Encryption.ExportPrivateKeyPem()));
        store.Save(EncryptionIssuedName, Encoding.UTF8.GetBytes(keys.EncryptionKeyIssuedOn.ToString("O", CultureInfo.InvariantCulture)));
    }

    private sealed record StoredRetiredKey(string Pem, DateTimeOffset RetiredOn);

    private static T Load<T>(ISecretStore store, string name, Func<string, T> fromPem, Func<T> create, Func<T, string> toPem)
    {
        if (store.Load(name) is { } pem)
        {
            return fromPem(Encoding.UTF8.GetString(pem));
        }

        var key = create();
        store.Save(name, Encoding.UTF8.GetBytes(toPem(key)));
        return key;
    }
}
