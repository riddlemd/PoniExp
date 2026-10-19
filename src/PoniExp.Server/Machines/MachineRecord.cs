using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Machines;

public sealed record MachineRecord(
    string Id,
    string Name,
    MachineDescription Description,
    string PublicKey,
    MembershipStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt = null,
    DateTimeOffset? LastSeen = null)
{
    public string JoinCode => KeyFingerprint.JoinCodeOf(PublicKey);
}
