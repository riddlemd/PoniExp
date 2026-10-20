using System.Text.Json;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Owner;

/// <summary>
/// The router's own identity and encryption keys (P-256, like a machine's), in <c>router-keys.json</c> in its data
/// directory (owner-only) and in backups. Machines pin them on first connect; admin commands are sealed to the
/// encryption key and their results signed with the identity key.
/// </summary>
public sealed class RouterKeys : IDisposable
{
    public const string FileName = "router-keys.json";

    public RouterKeys(MachineKeys keys) => Keys = keys;

    public MachineKeys Keys { get; }

    public string Id => Keys.MachineId;

    public static RouterKeys LoadOrCreate(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (File.Exists(path))
        {
            return new RouterKeys(FromFile(JsonSerializer.Deserialize<RouterKeyFile>(File.ReadAllText(path), WireProtocol.Json)!));
        }

        var keys = MachineKeys.Create();
        Directory.CreateDirectory(dataDirectory);
        PrivateFiles.WriteAllText(path, JsonSerializer.Serialize(ToFile(keys), WireProtocol.Json));
        return new RouterKeys(keys);
    }

    public RouterKeyFile Export() => ToFile(Keys);

    /// <summary>Writes keys from a backup (they're used after a restart). True if they differ from the ones in use.</summary>
    public bool Restore(string dataDirectory, RouterKeyFile file)
    {
        using var restored = FromFile(file);
        PrivateFiles.WriteAllText(Path.Combine(dataDirectory, FileName), JsonSerializer.Serialize(file, WireProtocol.Json));
        return restored.MachineId != Id;
    }

    public void Dispose() => Keys.Dispose();

    private static RouterKeyFile ToFile(MachineKeys keys) =>
        new(keys.Identity.ExportPrivateKeyPem(), keys.Encryption.ExportPrivateKeyPem(), keys.EncryptionKeyIssuedOn);

    private static MachineKeys FromFile(RouterKeyFile file) =>
        new(MachineIdentity.FromPem(file.Identity), EncryptionKey.FromPem(file.Encryption), file.IssuedOn == default ? DateTimeOffset.UnixEpoch : file.IssuedOn);
}

public sealed record RouterKeyFile(string Identity, string Encryption, DateTimeOffset IssuedOn = default);
