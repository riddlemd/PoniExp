using PoniExp.Core.Protocol;

namespace PoniExp.Core.Crypto;

/// <summary>
/// Both of a machine's keys: the identity key it signs with (its id is a hash of that key) and the
/// encryption key messages are sealed to, with the identity key's signature over it.
/// </summary>
public sealed class MachineKeys : IDisposable
{
    public MachineKeys(MachineIdentity identity, EncryptionKey encryption)
    {
        Identity = identity;
        Encryption = encryption;
        EncryptionKeySignature = identity.Sign(Signatures.EncryptionKeyPayload(identity.PublicKey, encryption.PublicKey));
    }

    public MachineIdentity Identity { get; }

    public EncryptionKey Encryption { get; }

    /// <summary>The identity key's signature over the encryption public key: what other machines check before encrypting to it.</summary>
    public string EncryptionKeySignature { get; }

    public string MachineId => Identity.MachineId;

    public string PublicKey => Identity.PublicKey;

    public string EncryptionPublicKey => Encryption.PublicKey;

    public static MachineKeys Create() => new(MachineIdentity.Create(), EncryptionKey.Create());

    public string Sign(ReadOnlySpan<byte> data) => Identity.Sign(data);

    public void Dispose()
    {
        Identity.Dispose();
        Encryption.Dispose();
    }
}
