using PoniExp.Core.Protocol;
using PoniExp.Server.Admin;
using PoniExp.Server.Machines;
using PoniExp.Server.Security;

namespace PoniExp.Server.Dashboard;

/// <summary>A machine as the dashboard shows it: its record and what waits in its queue.</summary>
public sealed record DashboardMachine(AdminMachineView Machine, int QueuedMessages, long QueuedBytes);

/// <summary>Everything the dashboard draws, read in one go from the router's snapshots.</summary>
public sealed record DashboardState(
    IReadOnlyList<DashboardMachine> Machines,
    IReadOnlyList<AdminBlobView> Blobs,
    IReadOnlyList<Job> Jobs,
    IReadOnlyList<RoomSummary> Rooms,
    IReadOnlyList<GroupInfo> Groups,
    AdminOwnerView Owner,
    bool TrafficLogOn,
    IReadOnlyList<TrafficRow> Traffic,
    string? CertificateSha256,
    DateTimeOffset ReadOn);

/// <summary>
/// What the dashboard reads and changes. Changes run the same <see cref="AdminService"/> code docker exec does, but without
/// the owner's signature per change: a live sign-in stands for it (docs/security.md). Revoke, owner set, group set,
/// backup and restore aren't here: daemons check the owner's signature on those, or they hold private keys.
/// </summary>
public sealed class DashboardActions(AdminService admin, MachineRegistry registry, MessageQueue queue, TrafficLog traffic,
    RouterCertificateInfo certificate, TimeProvider time, ILogger<DashboardActions> logger)
{
    public const int TrafficRows = 200;

    // Several log entries make one row (relayed, delivered, confirmed), so read enough for the rows to be whole.
    private const int TrafficEntriesRead = TrafficRows * 5;

    public DashboardState Read(bool finishedJobs)
    {
        var names = registry.List().ToDictionary(m => m.Id, m => m.Name);
        string Name(string? id) => id is null ? "" : names.GetValueOrDefault(id, id[..Math.Min(8, id.Length)]);
        var machines = admin.List()
            .Select(m => (View: m, Queued: queue.For(m.Id)))
            .Select(m => new DashboardMachine(m.View, m.Queued.Count, m.Queued.Sum(d => (long)d.Envelope.Size)))
            .OrderBy(m => m.Machine.Status).ThenBy(m => m.Machine.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var log = TrafficView.Rows(traffic.Read(null, null, TrafficEntriesRead)
            .Select(e => new AdminTrafficEntry(e.OccurredOn, e.Event, e.FromMachine is null ? null : Name(e.FromMachine),
                e.ToMachine is null ? null : Name(e.ToMachine), e.MessageId, e.Size, e.Sequence, e.Address, e.Detail)), TrafficRows);
        return new DashboardState(machines, admin.Blobs(), admin.Jobs(finishedJobs), admin.Rooms(), admin.Groups(), admin.Owner(), traffic.Enabled, log,
            certificate.Sha256, time.GetUtcNow());
    }

    public Task<AdminMachineView> ApproveAsync(DashboardSession session, string machineId) => RunAsync(session, $"approve {machineId}", () => admin.ApproveAsync(machineId));

    public Task<AdminMachineView> RejectAsync(DashboardSession session, string machineId) => RunAsync(session, $"reject {machineId}", () => admin.RejectAsync(machineId));

    public Task<AdminMachineView> RemoveAsync(DashboardSession session, string machineId) => RunAsync(session, $"remove {machineId}", () => admin.RemoveAsync(machineId));

    public Task<IReadOnlyList<AdminMachineView>> ClearPendingAsync(DashboardSession session) => RunAsync(session, "clear-pending", admin.ClearPendingAsync);

    public Task<int> ClearBlobsForAsync(DashboardSession session, string machineId) =>
        RunAsync(session, $"blobs clear --for {machineId}", () => Task.FromResult(admin.ClearBlobsFor(machineId)));

    public Task<Job> RemoveJobAsync(DashboardSession session, Guid job) => RunAsync(session, $"job remove {job:D}", () => admin.RemoveJobAsync(job.ToString("D")));

    public Task<bool> SetTrafficLogAsync(DashboardSession session, bool on) => RunAsync(session, $"traffic-log {(on ? "on" : "off")}", () =>
    {
        traffic.SetEnabled(on);
        return Task.FromResult(traffic.Enabled);
    });

    /// <summary>Whether <paramref name="session"/> may still make changes: not expired, and the owner key it signed in with is still the owner's.</summary>
    public bool IsLive(DashboardSession session) =>
        session.ExpiresOn > time.GetUtcNow() && admin.Owner().Fingerprint == session.Fingerprint;

    private async Task<T> RunAsync<T>(DashboardSession session, string command, Func<Task<T>> change)
    {
        if (!IsLive(session))
        {
            throw new RouterException("The dashboard sign-in has ended; run poniexp-client dashboard again.");
        }

        logger.LogInformation("Dashboard admin, signed in with {Fingerprint}: {Command}", session.Fingerprint, command);
        return await change();
    }
}
