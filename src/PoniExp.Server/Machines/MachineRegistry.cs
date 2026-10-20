using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Machines;

/// <summary>
/// Every machine that has asked to join, with its status. Reads are lock-free over an immutable snapshot;
/// writes are serialised and saved to the router's database before the new snapshot is published.
/// </summary>
public sealed class MachineRegistry
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly RouterStore _store;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly TimeProvider _time;
    private ImmutableDictionary<string, MachineRecord> _machines;

    public MachineRegistry(RouterStore store, IOptionsMonitor<RouterOptions> options, TimeProvider time)
    {
        _store = store;
        _options = options;
        _time = time;
        _machines = store.LoadMachines().ToImmutableDictionary(m => m.Id);
    }

    public MachineRecord? Get(string id) => _machines.GetValueOrDefault(id);

    public IReadOnlyList<MachineRecord> List() => _machines.Values.OrderBy(m => m.Status).ThenBy(m => m.Name).ToList();

    /// <summary>
    /// Records a verified join request. A pending request is refreshed; an approved or rejected machine
    /// keeps its status (remove it first to let it ask again).
    /// </summary>
    public async Task<MachineRecord> SubmitJoinAsync(JoinRequest request)
    {
        var id = KeyFingerprint.MachineIdOf(request.PublicKey);
        return await UpdateAsync(machines =>
        {
            var existing = machines.GetValueOrDefault(id);
            if (existing is { Status: MembershipStatus.Approved or MembershipStatus.Rejected })
            {
                return (machines, existing);
            }

            var now = _time.GetUtcNow();
            if (existing is null && machines.Values.Count(m => m.Status == MembershipStatus.Pending && !IsExpired(m, now))
                >= _options.CurrentValue.MaxPendingMachines)
            {
                throw new RouterException("Too many machines are waiting for approval; try again later.", RouterErrorKind.Conflict);
            }

            var record = new MachineRecord(id, request.Name, request.Description, request.PublicKey, request.EncryptionKey,
                request.EncryptionKeySignature, MembershipStatus.Pending, now);
            return (machines.SetItem(id, record), record);
        });
    }

    public Task<MachineRecord> ApproveAsync(string selector) => DecideAsync(selector, MembershipStatus.Approved);

    public Task<MachineRecord> RejectAsync(string selector) => DecideAsync(selector, MembershipStatus.Rejected);

    public Task<MachineRecord> RemoveAsync(string selector) =>
        UpdateAsync(machines =>
        {
            var record = Resolve(machines, selector);
            return (machines.Remove(record.Id), record);
        });

    /// <summary>True if the machine has no live request: never asked, or its pending request expired.</summary>
    public bool IsNewRequest(string id) => Get(id) is not { } m || (m.Status == MembershipStatus.Pending && IsExpired(m, _time.GetUtcNow()));

    /// <summary>Forgets pending requests older than <see cref="RouterOptions.JoinRequestExpiry"/>; returns them.</summary>
    public async Task<IReadOnlyList<MachineRecord>> ExpirePendingAsync()
    {
        var expired = new List<MachineRecord>();
        await UpdateAsync(machines =>
        {
            var now = _time.GetUtcNow();
            expired.AddRange(machines.Values.Where(m => m.Status == MembershipStatus.Pending && IsExpired(m, now)));
            return (expired.Count == 0 ? machines : machines.RemoveRange(expired.Select(m => m.Id)), null!);
        });
        return expired;
    }

    /// <summary>Forgets every pending request (<c>poniexp admin clear-pending</c>); returns them.</summary>
    public async Task<IReadOnlyList<MachineRecord>> ClearPendingAsync()
    {
        var cleared = new List<MachineRecord>();
        await UpdateAsync(machines =>
        {
            cleared.AddRange(machines.Values.Where(m => m.Status == MembershipStatus.Pending));
            return (cleared.Count == 0 ? machines : machines.RemoveRange(cleared.Select(m => m.Id)), null!);
        });
        return cleared;
    }

    /// <summary>Puts machines back from a backup, replacing any with the same id.</summary>
    public Task RestoreAsync(IReadOnlyCollection<MachineRecord> records) =>
        UpdateAsync(machines => (machines.SetItems(records.Select(r => KeyValuePair.Create(r.Id, r))), null!));

    private bool IsExpired(MachineRecord pending, DateTimeOffset now) => now - pending.RequestedOn > _options.CurrentValue.JoinRequestExpiry;

    /// <summary>What the machine's daemon reported it has (job requirements are checked against it).</summary>
    public Task SetCapabilitiesAsync(string id, MachineCapabilities capabilities) =>
        UpdateAsync(machines => machines.TryGetValue(id, out var record)
            ? (machines.SetItem(id, record with { Capabilities = capabilities }), record)
            : (machines, null!));

    public Task TouchAsync(string id, DateTimeOffset seenOn) =>
        UpdateAsync(machines => machines.TryGetValue(id, out var record)
            ? (machines.SetItem(id, record with { LastSeenOn = seenOn }), record)
            : (machines, null!));

    /// <summary>For tests that play a misbehaving router: hand out another encryption key for a machine.</summary>
    internal Task SubstituteEncryptionKeyAsync(string id, string encryptionKey, string signature) =>
        UpdateAsync(machines => (machines.SetItem(id, machines[id] with { EncryptionKey = encryptionKey, EncryptionKeySignature = signature }),
            machines[id]));

    /// <summary>
    /// Finds a machine by full id, by an id prefix of at least 4 characters, or by name. Ambiguity is an
    /// error, never a guess: approving the wrong machine is the one mistake this list exists to prevent.
    /// </summary>
    public MachineRecord Resolve(string selector) => Resolve(_machines, selector);

    private static MachineRecord Resolve(ImmutableDictionary<string, MachineRecord> machines, string selector)
    {
        if (machines.TryGetValue(selector, out var exact))
        {
            return exact;
        }

        var matches = machines.Values
            .Where(m => m.Name == selector || (selector.Length >= 4 && m.Id.StartsWith(selector, StringComparison.Ordinal)))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new RouterException($"No machine matches '{selector}'.", RouterErrorKind.NotFound),
            _ => throw new RouterException(
                $"'{selector}' matches {matches.Count} machines ({string.Join(", ", matches.Select(m => m.Id[..8]))}); use the id.",
                RouterErrorKind.Conflict),
        };
    }

    private Task<MachineRecord> DecideAsync(string selector, MembershipStatus status) =>
        UpdateAsync(machines =>
        {
            var record = Resolve(machines, selector);
            if (status == MembershipStatus.Approved)
            {
                var clash = machines.Values.FirstOrDefault(m =>
                    m.Id != record.Id && m.Name == record.Name && m.Status == MembershipStatus.Approved);
                if (clash is not null)
                {
                    throw new RouterException(
                        $"An approved machine is already named '{record.Name}' ({clash.Id[..8]}); remove it first.",
                        RouterErrorKind.Conflict);
                }
            }

            var decided = record with { Status = status, DecidedOn = _time.GetUtcNow() };
            return (machines.SetItem(record.Id, decided), decided);
        });

    private async Task<MachineRecord> UpdateAsync(
        Func<ImmutableDictionary<string, MachineRecord>, (ImmutableDictionary<string, MachineRecord> Next, MachineRecord Result)> change)
    {
        await _lock.WaitAsync();
        try
        {
            var (next, result) = change(_machines);
            if (!ReferenceEquals(next, _machines))
            {
                _store.SaveMachines(_machines, next);
                _machines = next;
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }
}
