using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Machines;

public sealed record MachineRecord(
    string Id,
    string Name,
    MachineDescription Description,
    string PublicKey,
    string EncryptionKey,
    string EncryptionKeySignature,
    MembershipStatus Status,
    DateTimeOffset RequestedOn,
    DateTimeOffset? DecidedOn = null,
    DateTimeOffset? LastSeenOn = null,
    MachineCapabilities? Capabilities = null)
{
    public string JoinCode => KeyFingerprint.JoinCodeOf(PublicKey);
}
