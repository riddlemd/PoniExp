using PoniExp.Core.Crypto;

namespace PoniExp.Core.Protocol;

/// <summary>
/// A one-time dashboard sign-in (docs/plan.md, Phase 13), signed with the owner's SSH key by <c>poniexp-client dashboard</c>
/// and carried in the link it opens. The router takes it once, within the clock skew, if it names this router.
/// </summary>
public sealed record DashboardSignIn(Guid Id, string RouterId, DateTimeOffset IssuedOn, string Signature)
{
    public const string Path = "/signin";

    public static byte[] Payload(Guid id, string routerId, DateTimeOffset issuedOn) =>
        SigningPayload.Build("poniexp-dashboard-signin", id.ToString("D"), routerId, Signatures.Stamp(issuedOn));

    public byte[] Payload() => Payload(Id, RouterId, IssuedOn);

    public bool IsSignedBy(OwnerKey owner) => owner.Verify(Payload(), Signature);

    /// <summary>The sign-in link under <paramref name="dashboard"/> (the dashboard's base URL).</summary>
    public Uri Link(Uri dashboard) =>
        new(dashboard, $"{Path}?id={Id:D}&router={Uri.EscapeDataString(RouterId)}&issued={Uri.EscapeDataString(Signatures.Stamp(IssuedOn))}" +
                       $"&signature={Uri.EscapeDataString(Signature)}");

    /// <summary>Null when a field is missing or malformed.</summary>
    public static DashboardSignIn? FromQuery(string? id, string? router, string? issued, string? signature) =>
        Guid.TryParse(id, out var parsed) && !string.IsNullOrEmpty(router) && !string.IsNullOrEmpty(signature)
        && DateTimeOffset.TryParse(issued, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var on)
            ? new DashboardSignIn(parsed, router, on, signature)
            : null;
}
