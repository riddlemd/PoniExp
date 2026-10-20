using PoniExp.Core.Protocol;

namespace PoniExp.Core.Crypto;

/// <summary>
/// Both of a machine's keys: the identity key it signs with (its id is a hash of that key) and the encryption key
/// messages are sealed to, with the identity key's signature over it and when it was issued. The encryption key can be
/// rotated (phase 11); retired ones are kept to open older envelopes until retention drops them.
/// </summary>
public sealed class MachineKeys : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<RetiredEncryptionKey> _retired;

    public MachineKeys(MachineIdentity identity, EncryptionKey encryption, DateTimeOffset? issuedOn = null, IEnumerable<RetiredEncryptionKey>? retired = null)
    {
        Identity = identity;
        Encryption = encryption;
        EncryptionKeyIssuedOn = Signatures.Truncate(issuedOn ?? DateTimeOffset.UtcNow);
        EncryptionKeySignature = identity.Sign(Signatures.EncryptionKeyPayload(identity.PublicKey, encryption.PublicKey, EncryptionKeyIssuedOn));
        _retired = [.. retired ?? []];
    }

    public MachineIdentity Identity { get; }

    public EncryptionKey Encryption { get; private set; }

    /// <summary>When the current encryption key was issued (signed with it).</summary>
    public DateTimeOffset EncryptionKeyIssuedOn { get; private set; }

    /// <summary>The identity key's signature over the encryption public key and its issue time: what other machines check before encrypting to it.</summary>
    public string EncryptionKeySignature { get; private set; }

    public string MachineId => Identity.MachineId;

    public string PublicKey => Identity.PublicKey;

    public string EncryptionPublicKey => Encryption.PublicKey;

    /// <summary>Earlier encryption keys, newest first, with when each was retired.</summary>
    public IReadOnlyList<RetiredEncryptionKey> RetiredEncryptionKeys
    {
        get
        {
            lock (_lock)
            {
                return [.. _retired.OrderByDescending(r => r.RetiredOn)];
            }
        }
    }

    /// <summary>Every key that may open an envelope: the current one first, then the retired ones, newest first.</summary>
    public IReadOnlyList<EncryptionKey> DecryptionKeys => [Encryption, .. RetiredEncryptionKeys.Select(r => r.Key)];

    public static MachineKeys Create() => new(MachineIdentity.Create(), EncryptionKey.Create());

    public string Sign(ReadOnlySpan<byte> data) => Identity.Sign(data);

    /// <summary>A new encryption key, issued now; the old one is retired (kept to open older envelopes).</summary>
    public void RotateEncryption(EncryptionKey next, DateTimeOffset now)
    {
        lock (_lock)
        {
            var issued = Signatures.Truncate(now);
            if (issued <= EncryptionKeyIssuedOn)
            {
                issued = EncryptionKeyIssuedOn.AddMilliseconds(1);
            }

            _retired.Add(new RetiredEncryptionKey(Encryption, issued));
            Encryption = next;
            EncryptionKeyIssuedOn = issued;
            EncryptionKeySignature = Identity.Sign(Signatures.EncryptionKeyPayload(Identity.PublicKey, next.PublicKey, issued));
        }
    }

    /// <summary>Forgets retired keys retired before <paramref name="before"/>; returns how many.</summary>
    public int DropRetiredBefore(DateTimeOffset before)
    {
        lock (_lock)
        {
            var old = _retired.Where(r => r.RetiredOn < before).ToList();
            foreach (var r in old)
            {
                _retired.Remove(r);
                r.Key.Dispose();
            }

            return old.Count;
        }
    }

    public void Dispose()
    {
        Identity.Dispose();
        Encryption.Dispose();
        foreach (var r in _retired)
        {
            r.Key.Dispose();
        }
    }
}

public sealed record RetiredEncryptionKey(EncryptionKey Key, DateTimeOffset RetiredOn);
