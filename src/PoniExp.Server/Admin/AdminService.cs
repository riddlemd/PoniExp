using Microsoft.AspNetCore.SignalR;
using PoniExp.Core.Protocol;
using PoniExp.Server.Hubs;
using PoniExp.Server.Machines;

namespace PoniExp.Server.Admin;

/// <summary>Approve, reject and remove machines, and tell their live connections.</summary>
public sealed class AdminService(
    MachineRegistry registry,
    MessageQueue queue,
    PresenceTracker presence,
    IHubContext<RelayHub> hub,
    ILogger<AdminService> logger)
{
    public IReadOnlyList<AdminMachineView> List() => registry.List().Select(View).ToList();

    public async Task<AdminMachineView> ApproveAsync(string selector)
    {
        var record = await registry.ApproveAsync(selector);
        logger.LogInformation("Approved {Name} ({Id})", record.Name, record.Id[..8]);
        await hub.Clients.Group(RelayHub.KeyGroup(record.Id)).SendAsync(HubMethods.MembershipChanged, record.Status);
        return View(record);
    }

    public async Task<AdminMachineView> RejectAsync(string selector)
    {
        var record = await registry.RejectAsync(selector);
        logger.LogInformation("Rejected {Name} ({Id})", record.Name, record.Id[..8]);
        await hub.Clients.Group(RelayHub.KeyGroup(record.Id)).SendAsync(HubMethods.MembershipChanged, record.Status);
        presence.Disconnect(record.Id);
        await queue.DropForAsync(record.Id);
        return View(record);
    }

    public async Task<AdminMachineView> RemoveAsync(string selector)
    {
        var record = await registry.RemoveAsync(selector);
        logger.LogInformation("Removed {Name} ({Id})", record.Name, record.Id[..8]);
        await hub.Clients.Group(RelayHub.KeyGroup(record.Id)).SendAsync(HubMethods.MembershipChanged, MembershipStatus.Unknown);
        presence.Disconnect(record.Id);
        await queue.DropForAsync(record.Id);
        return View(record) with { Status = MembershipStatus.Unknown };
    }

    private AdminMachineView View(MachineRecord m) =>
        new(m.Id, m.Name, m.Status, m.JoinCode, m.Description, m.RequestedAt, m.DecidedAt, m.LastSeen, presence.IsOnline(m.Id));
}

public sealed record AdminMachineView(
    string Id,
    string Name,
    MembershipStatus Status,
    string JoinCode,
    MachineDescription Description,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? LastSeen,
    bool Online);
