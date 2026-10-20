using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server.Hubs;
using PoniExp.Server.Machines;

namespace PoniExp.Server.Owner;

/// <summary>
/// The owner's public key (set by <c>poniexp admin owner set</c>) and the current approval desk (an owner-signed
/// <see cref="HereClaim"/>). The router passes the desk on; every daemon checks the signature against its own pin.
/// </summary>
public sealed class OwnerDesk(RouterStore store, MachineRegistry machines, IHubContext<RelayHub> hub, IOptionsMonitor<RouterOptions> options,
    TimeProvider time, ILogger<OwnerDesk> logger)
{
    private const string OwnerState = "owner_key";
    private const string HereState = "here";
    private readonly Lock _lock = new();

    public OwnerKey? Owner => store.GetState(OwnerState) is { } line ? OwnerKey.Parse(line) : null;

    /// <summary>The newest claim, even if it's away or expired (daemons need it to judge older ones).</summary>
    public HereClaim? Latest => store.GetState(HereState) is { } json ? JsonSerializer.Deserialize<HereClaim>(json, WireProtocol.Json) : null;

    /// <summary>The desk machine now: the newest claim, unexpired, naming a machine still approved.</summary>
    public string? Desk => Latest?.DeskAt(time.GetUtcNow()) is { } m && machines.Get(m) is { Status: MembershipStatus.Approved } ? m : null;

    /// <exception cref="FormatException">Not an ssh-ed25519 public key line.</exception>
    public OwnerKey SetOwner(string line)
    {
        var key = OwnerKey.Parse(line);
        lock (_lock)
        {
            store.SetState(OwnerState, key.ToString());
        }

        logger.LogInformation("Owner key set: {Fingerprint} {Comment}", key.Fingerprint, key.Comment);
        return key;
    }

    /// <summary>Takes a new owner-signed desk claim and sends it to every machine.</summary>
    public async Task<HereClaim> SetHereAsync(string from, HereClaim claim)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            var owner = Owner ?? throw new RouterException("The router has no owner key yet (poniexp admin owner set).");
            if (claim is null || claim.Problem(owner) is { } problem)
            {
                throw new RouterException($"Refused desk: {claim?.Problem(owner) ?? "empty"}.");
            }

            if ((now - claim.IssuedOn).Duration() > options.CurrentValue.ClockSkew || (claim.MachineId is not null && claim.ExpiresOn <= now))
            {
                throw new RouterException("Refused desk: its time is off (check this machine's clock) or it has expired.");
            }

            if (Latest is { } latest && claim.IssuedOn <= latest.IssuedOn)
            {
                throw new RouterException("Refused desk: a newer one is in place.");
            }

            if (claim.MachineId is { } m && machines.Get(m) is not { Status: MembershipStatus.Approved })
            {
                throw new RouterException("Refused desk: that machine isn't approved.");
            }

            store.SetState(HereState, JsonSerializer.Serialize(claim, WireProtocol.Json));
        }

        logger.LogInformation("Desk {Desk} (signed by the owner, sent by {From})", claim.MachineId is { } d ? machines.Get(d)?.Name ?? d[..8] : "away",
            machines.Get(from)?.Name ?? from[..8]);
        await hub.Clients.Group(RelayHub.MembersGroup).SendAsync(HubMethods.HereChanged, claim);
        return claim;
    }

    /// <summary>
    /// The desk machine steps down without the owner's key: recorded as an unsigned "away" that only ends the desk
    /// (daemons accept an unsigned away from the router: at worst a router could stop approvals, never grant one).
    /// </summary>
    public async Task LeaveAsync(string from)
    {
        HereClaim away;
        lock (_lock)
        {
            if (Desk != from)
            {
                throw new RouterException("This machine isn't the desk.");
            }

            var now = time.GetUtcNow();
            away = new HereClaim(null, now > Latest!.IssuedOn ? now : Latest.IssuedOn.AddTicks(1), now, "");
            store.SetState(HereState, JsonSerializer.Serialize(away, WireProtocol.Json));
        }

        logger.LogInformation("Desk {Name} left", machines.Get(from)?.Name ?? from[..8]);
        await hub.Clients.Group(RelayHub.MembersGroup).SendAsync(HubMethods.HereChanged, away);
    }
}
