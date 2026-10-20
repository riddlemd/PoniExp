using PoniExp.Core.Crypto;

namespace PoniExp.Core.Protocol;

/// <summary>
/// The owner's approval desk (see docs/plan.md, Phase 9): "the owner is at <see cref="MachineId"/> until
/// <see cref="ExpiresOn"/>", signed with the owner's SSH key. A null machine means away (no desk).
/// </summary>
public sealed record HereClaim(string? MachineId, DateTimeOffset IssuedOn, DateTimeOffset ExpiresOn, string Signature)
{
    /// <summary>The longest a desk may last.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    public static byte[] Payload(string? machineId, DateTimeOffset issuedOn, DateTimeOffset expiresOn) =>
        System.Text.Encoding.UTF8.GetBytes($"poniexp-here\n{machineId ?? "away"}\n{Signatures.Stamp(issuedOn)}\n{Signatures.Stamp(expiresOn)}");

    public byte[] Payload() => Payload(MachineId, IssuedOn, ExpiresOn);

    /// <summary>Null if <paramref name="owner"/> signed it and it's well formed (not checked: whether it's current), else why not.</summary>
    public string? Problem(OwnerKey owner)
    {
        if (ExpiresOn < IssuedOn || ExpiresOn - IssuedOn > MaxLifetime)
        {
            return "a desk lasts at most 7 days";
        }

        return owner.Verify(Payload(), Signature) ? null : "not signed by the owner's key";
    }

    /// <summary>The desk machine at <paramref name="now"/>, or null (away, or expired).</summary>
    public string? DeskAt(DateTimeOffset now) => MachineId is { } m && now < ExpiresOn ? m : null;
}

/// <summary>What an <c>approval-request</c> carries: who asks, for what. <see cref="Command"/> is a shell command, when the tool runs one.</summary>
public sealed record ApprovalRequestBody(Guid RequestId, string Agent, string Summary, string? Details, string? Tool, string? Command,
    DateTimeOffset RequestedOn);

/// <summary>The desk's answer. <see cref="Always"/>: the requesting machine makes an "always allow" rule for that agent and tool.</summary>
public sealed record ApprovalAnswerBody(Guid RequestId, bool Approved, bool Always = false, string? Reason = null);

/// <summary>The agent gave up waiting.</summary>
public sealed record ApprovalWithdrawnBody(Guid RequestId);
