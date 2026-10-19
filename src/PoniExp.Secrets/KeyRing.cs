using System.Text;
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
        return new MachineKeys(identity, encryption);
    }

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
