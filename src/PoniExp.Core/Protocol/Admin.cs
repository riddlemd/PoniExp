using System.Security.Cryptography;
using System.Text;
using PoniExp.Core.Crypto;

namespace PoniExp.Core.Protocol;

/// <summary>
/// A <c>poniexp admin</c> command from any machine (see docs/plan.md, Phase 10), signed with the owner's SSH key.
/// <see cref="Input"/> is what <c>restore -</c> reads. The router runs it once, within the clock skew, if it names this router.
/// </summary>
public sealed record AdminCommand(Guid Id, string RouterId, IReadOnlyList<string> Args, string? Input, DateTimeOffset IssuedOn, string Signature)
{
    public const string SealPurpose = "admin";
    public const string ResultPurpose = "admin-result";

    public static byte[] Payload(Guid id, string routerId, IReadOnlyList<string> args, string? input, DateTimeOffset issuedOn) =>
        SigningPayload.Build("poniexp-admin", [id.ToString("D"), routerId, Signatures.Stamp(issuedOn),
            input is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))), .. args]);

    public byte[] Payload() => Payload(Id, RouterId, Args, Input, IssuedOn);

    public bool IsSignedBy(OwnerKey owner) => owner.Verify(Payload(), Signature);

    /// <summary>The new key line if this is <c>owner set &lt;key&gt;</c>.</summary>
    public string? NewOwnerKey => Args.Count > 2 && Args[0] == "owner" && Args[1] == "set" ? string.Join(" ", Args.Skip(2)) : null;

    /// <summary>Its group's name and members if this is <c>group set &lt;name&gt; &lt;members&gt;</c>.</summary>
    public (string Name, IReadOnlyList<string> Members)? GroupSet =>
        Args.Count > 3 && Args[0] == "group" && Args[1] == "set" ? (Args[2], [.. Args.Skip(3)]) : null;
}

/// <summary>What the router answers: the exit code and output, as <c>docker exec</c> would print them.</summary>
public sealed record AdminResult(Guid CommandId, int ExitCode, string Output);

/// <summary>The result sealed to the asking machine, and the router's identity signature over the sealed form.</summary>
public sealed record SignedAdminResult(SealedBox Box, string Signature)
{
    public static byte[] Payload(Guid commandId, string machineId, SealedBox box) =>
        SigningPayload.Build("admin-result", commandId.ToString("D"), machineId, box.EphemeralKey, box.Ciphertext);
}

/// <summary>A machine waiting to join, as the desk sees it.</summary>
public sealed record PendingJoin(string MachineId, string Name, string JoinCode, string OperatingSystem, string HostName, string? Notes, DateTimeOffset RequestedOn);
