using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;
using PoniExp.Server.Hubs;
using PoniExp.Server.Machines;
using PoniExp.Server.Security;

namespace PoniExp.Server.Admin;

/// <summary>Approve, reject and remove machines, and tell their live connections.</summary>
public sealed class AdminService(
    MachineRegistry registry,
    MessageQueue queue,
    PresenceTracker presence,
    ConnectionGate gate,
    RouterStore store,
    Blobs.BlobRegistry blobs,
    Rooms.RoomRegistry rooms,
    Jobs.JobBoard jobs,
    Owner.OwnerDesk desk,
    Owner.RouterKeys routerKeys,
    IHubContext<RelayHub> hub,
    IOptionsMonitor<RouterOptions> options,
    TimeProvider time,
    ILogger<AdminService> logger)
{
    public const string BackupFormat = "poniexp-router-backup";

    public IReadOnlyList<AdminMachineView> List() => registry.List().Select(View).ToList();

    public async Task<AdminMachineView> ApproveAsync(string selector)
    {
        var record = await registry.ApproveAsync(selector);
        logger.LogInformation("Approved {Name} ({Id})", record.Name, record.Id[..8]);
        await hub.Clients.Group(RelayHub.KeyGroup(record.Id)).SendAsync(HubMethods.MembershipChanged, record.Status);
        await desk.PushPendingJoinsAsync();
        return View(record);
    }

    public async Task<AdminMachineView> RejectAsync(string selector)
    {
        var record = await registry.RejectAsync(selector);
        logger.LogInformation("Rejected {Name} ({Id})", record.Name, record.Id[..8]);
        await hub.Clients.Group(RelayHub.KeyGroup(record.Id)).SendAsync(HubMethods.MembershipChanged, record.Status);
        presence.Disconnect(record.Id);
        await queue.DropForAsync(record.Id);
        await desk.PushPendingJoinsAsync();
        return View(record);
    }

    public async Task<AdminMachineView> RemoveAsync(string selector)
    {
        var record = await registry.RemoveAsync(selector);
        logger.LogInformation("Removed {Name} ({Id})", record.Name, record.Id[..8]);
        await hub.Clients.Group(RelayHub.KeyGroup(record.Id)).SendAsync(HubMethods.MembershipChanged, MembershipStatus.Unknown);
        presence.Disconnect(record.Id);
        await queue.DropForAsync(record.Id);
        blobs.Forget(record.Id);
        await rooms.MachineRemovedAsync(record.Id);
        await desk.PushPendingJoinsAsync();
        return View(record) with { Status = MembershipStatus.Unknown };
    }

    /// <summary>Forgets every pending join request and tells the waiting clients (<c>poniexp admin clear-pending</c>).</summary>
    public async Task<IReadOnlyList<AdminMachineView>> ClearPendingAsync()
    {
        var cleared = await ForgetAsync(await registry.ClearPendingAsync(), "Cleared");
        await desk.PushPendingJoinsAsync();
        return cleared;
    }

    /// <summary>Forgets join requests that waited longer than <see cref="RouterOptions.JoinRequestExpiry"/>.</summary>
    public async Task<IReadOnlyList<AdminMachineView>> ExpirePendingAsync()
    {
        var expired = await ForgetAsync(await registry.ExpirePendingAsync(), "Expired");
        if (expired.Count > 0)
        {
            await desk.PushPendingJoinsAsync();
        }

        return expired;
    }

    /// <summary>The certificate (with its private key) and the approved machines: what can't be rebuilt without every client.</summary>
    public RouterBackup Backup(RouterCertificateInfo certificate)
    {
        var pfx = Path.Combine(options.CurrentValue.DataDirectory, "router.pfx");
        return new RouterBackup(BackupFormat, 1, time.GetUtcNow(), certificate.Sha256,
            File.Exists(pfx) ? Convert.ToBase64String(File.ReadAllBytes(pfx)) : null,
            [.. registry.List().Where(m => m.Status == MembershipStatus.Approved)],
            [.. jobs.List(includeFinished: false)], desk.Owner?.ToString(), routerKeys.Export(), [.. desk.Handovers]);
    }

    /// <summary>
    /// Puts a backup back: the approved machines at once, the certificate on disk (served after a restart).
    /// Returns whether the certificate changed.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(RouterBackup backup, RouterCertificateInfo current)
    {
        if (backup.Format != BackupFormat || backup.Version != 1)
        {
            throw new RouterException("That isn't a PoniExp router backup (or it's from a newer version).", RouterErrorKind.Invalid);
        }

        var certificateChanged = false;
        if (backup.Certificate is { } certificate)
        {
            var bytes = Convert.FromBase64String(certificate);
            using var loaded = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(bytes, null);
            if (RouterCertificate.Sha256Of(loaded) != backup.CertificateSha256)
            {
                throw new RouterException("The backup's certificate doesn't match its SHA-256; the file is damaged.", RouterErrorKind.Invalid);
            }

            certificateChanged = backup.CertificateSha256 != current.Sha256;
            PrivateFiles.WriteAllBytes(Path.Combine(options.CurrentValue.DataDirectory, "router.pfx"), bytes);
        }

        var machines = backup.Machines.Where(m => m.Status == MembershipStatus.Approved && KeyFingerprint.MachineIdOf(m.PublicKey) == m.Id).ToList();
        await registry.RestoreAsync(machines);
        var restoredJobs = jobs.Restore(backup.Jobs ?? []);
        if (backup.OwnerKey is { } ownerKey)
        {
            desk.SetOwner(ownerKey);
        }

        var keysChanged = backup.RouterKeys is { } keys && routerKeys.Restore(options.CurrentValue.DataDirectory, keys);
        if (backup.OwnerHandovers is { Count: > 0 } handovers)
        {
            desk.RestoreHandovers(handovers);
        }
        logger.LogInformation("Restored {Count} machines from a backup of {On}; certificate {Changed}", machines.Count, backup.CreatedOn,
            certificateChanged ? "changed (restart to serve it)" : "unchanged");
        return new RestoreResult(machines.Count, certificateChanged, backup.CertificateSha256, restoredJobs, keysChanged);
    }

    private async Task<IReadOnlyList<AdminMachineView>> ForgetAsync(IReadOnlyList<MachineRecord> forgotten, string verb)
    {
        foreach (var record in forgotten)
        {
            logger.LogInformation("{Verb} the join request of {Name} ({Id})", verb, record.Name, record.Id[..8]);
            await hub.Clients.Group(RelayHub.KeyGroup(record.Id)).SendAsync(HubMethods.MembershipChanged, MembershipStatus.Unknown);
            gate.Expel(record.Id);
        }

        return [.. forgotten.Select(m => View(m) with { Status = MembershipStatus.Unknown })];
    }

    /// <summary>The groups, each with the owner-signed command that set it, if it was set that way.</summary>
    public IReadOnlyList<GroupInfo> Groups() => store.LoadGroups();

    internal static string GroupSignatureState(string group) => RouterStore.GroupSignatureState(group);

    public IReadOnlyList<RoomSummary> Rooms() => rooms.Summaries();

    /// <summary>The owner key and the desk, for <c>poniexp admin owner</c>.</summary>
    public AdminOwnerView Owner() =>
        new(desk.Owner?.ToString(), desk.Owner?.Fingerprint, desk.Desk is { } d ? registry.Get(d)?.Name ?? d : null, desk.Desk is null ? null : desk.Latest?.ExpiresOn);

    /// <exception cref="FormatException">Not an ssh-ed25519 public key line.</exception>
    public AdminOwnerView SetOwner(string line)
    {
        desk.SetOwner(line);
        return Owner();
    }

    /// <summary>Takes a job off the board, by id or id prefix (at least 4 characters).</summary>
    public async Task<Job> RemoveJobAsync(string selector)
    {
        var matches = jobs.List(includeFinished: true).Where(j => j.Id.ToString() == selector || (selector.Length >= 4 && j.Id.ToString().StartsWith(selector, StringComparison.Ordinal))).ToList();
        return matches switch
        {
            [var one] => await jobs.RemoveAsync(one.Id),
            [] => throw new RouterException($"No job {selector}.", RouterErrorKind.NotFound),
            _ => throw new RouterException($"'{selector}' matches {matches.Count} jobs; use more of its id."),
        };
    }

    /// <summary>The board, members shown as machine-name/agent.</summary>
    public IReadOnlyList<Job> Jobs(bool includeFinished) =>
        [.. jobs.List(includeFinished).Select(j => j with { Poster = rooms.Display(j.Poster), Claimant = j.Claimant is { } c ? rooms.Display(c) : null })];

    /// <summary>Collapsed rooms' statistics, members shown by machine name.</summary>
    public IReadOnlyList<RoomStats> CollapsedRooms(DateTimeOffset? since) =>
        [.. rooms.Collapsed(since).Select(s => s with
        {
            Members = [.. s.Members.Select(m => m with { Member = rooms.Display(m.Member) })],
            Machines = [.. s.Machines.Select(m => m with { Machine = registry.Get(m.Machine)?.Name ?? m.Machine[..Math.Min(8, m.Machine.Length)] })],
            Messages = [.. s.Messages.Select(m => m with { FromMachine = registry.Get(m.FromMachine)?.Name ?? m.FromMachine[..Math.Min(8, m.FromMachine.Length)] })],
        })];

    /// <summary>
    /// Sets a group (<c>poniexp admin group set</c>): members are <c>machine/agent</c> (an approved machine, by name).
    /// Groups don't nest. No members removes it.
    /// </summary>
    public GroupInfo SetGroup(string name, IReadOnlyList<string> members)
    {
        if (!Limits.IsValidAgent(name))
        {
            throw new RouterException($"A group name is {Limits.AgentRule}.");
        }

        if (members.Count > 256)
        {
            throw new RouterException("A group has at most 256 members.");
        }

        foreach (var member in members)
        {
            try
            {
                if (Address.ParseIndirect(member) is not null)
                {
                    throw new RouterException($"'{member}': groups don't nest; list its members.");
                }

                var address = Address.Parse(member);
                if (address.Agent is null || registry.List().All(m => m.Name != address.Machine || m.Status != MembershipStatus.Approved))
                {
                    throw new RouterException($"'{member}': a member is machine/agent, on an approved machine.");
                }
            }
            catch (FormatException e)
            {
                throw new RouterException(e.Message);
            }
        }

        var group = new GroupInfo(name, [.. members.Distinct()]);
        store.SaveGroup(name, group.Members);
        store.SetState(GroupSignatureState(name), ""); // a remote, signed set puts its signature back after this
        logger.LogInformation("Group {Name}: {Members}", name, group.Members.Count == 0 ? "removed" : string.Join(", ", group.Members));
        return group;
    }

    /// <summary>Every blob, with machine names (<c>poniexp admin blobs</c>).</summary>
    public IReadOnlyList<AdminBlobView> Blobs()
    {
        string Name(string id) => registry.Get(id)?.Name ?? id[..Math.Min(8, id.Length)];
        return [.. blobs.List().Select(b => new AdminBlobView(b.Id, Name(b.Sender), b.State, b.ReservedBytes, b.ChunkCount, [.. b.SharedWith.Select(Name)],
            [.. b.ReleasedBy.Select(Name)], b.CreatedOn, b.ExpiresOn, b.EndReason))];
    }

    /// <summary>Releases everything waiting for a machine (<c>poniexp admin blobs clear --for</c>).</summary>
    public int ClearBlobsFor(string selector) => blobs.ClearFor(registry.Resolve(selector).Id);

    private AdminMachineView View(MachineRecord m) =>
        new(m.Id, m.Name, m.Status, m.JoinCode, m.Description, m.RequestedOn, m.DecidedOn, m.LastSeenOn, presence.IsOnline(m.Id), m.Capabilities);
}

public sealed record AdminMachineView(
    string Id,
    string Name,
    MembershipStatus Status,
    string JoinCode,
    MachineDescription Description,
    DateTimeOffset RequestedOn,
    DateTimeOffset? DecidedOn,
    DateTimeOffset? LastSeenOn,
    bool Online,
    MachineCapabilities? Capabilities = null);

/// <summary>A router backup: its TLS certificate (PKCS#12, base64, with the private key) and the approved machines.</summary>
public sealed record RouterBackup(string Format, int Version, DateTimeOffset CreatedOn, string? CertificateSha256, string? Certificate,
    IReadOnlyList<MachineRecord> Machines, IReadOnlyList<Job>? Jobs = null, string? OwnerKey = null, Owner.RouterKeyFile? RouterKeys = null,
    IReadOnlyList<AdminCommand>? OwnerHandovers = null);

public sealed record RestoreResult(int Machines, bool CertificateChanged, string? CertificateSha256, int Jobs = 0, bool RouterKeysChanged = false);

public sealed record AdminBlobView(Guid Id, string Sender, BlobState State, long Bytes, int Chunks, IReadOnlyList<string> SharedWith,
    IReadOnlyList<string> ReleasedBy, DateTimeOffset CreatedOn, DateTimeOffset ExpiresOn, string? EndReason);

public sealed record AdminOwnerView(string? Key, string? Fingerprint, string? Desk, DateTimeOffset? DeskUntil);
