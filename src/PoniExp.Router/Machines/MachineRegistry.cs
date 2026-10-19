using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Router.Machines;

/// <summary>
/// Every machine that has asked to join, with its status. Reads are lock-free over an immutable snapshot;
/// writes are serialised and saved to machines.json before the new snapshot is published.
/// </summary>
public sealed class MachineRegistry
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path;
    private readonly int _maxPending;
    private readonly TimeProvider _time;
    private ImmutableDictionary<string, MachineRecord> _machines;

    public MachineRegistry(IOptions<RouterOptions> options, TimeProvider time)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _path = Path.Combine(options.Value.DataDirectory, "machines.json");
        _maxPending = options.Value.MaxPendingMachines;
        _time = time;
        _machines = (JsonFile.Read<List<MachineRecord>>(_path) ?? []).ToImmutableDictionary(m => m.Id);
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

            if (existing is null && machines.Values.Count(m => m.Status == MembershipStatus.Pending) >= _maxPending)
            {
                throw new RouterException("Too many machines are waiting for approval; try again later.", RouterErrorKind.Conflict);
            }

            var record = new MachineRecord(id, request.Name, request.Description, request.PublicKey, MembershipStatus.Pending,
                _time.GetUtcNow());
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

    public Task TouchAsync(string id, DateTimeOffset at) =>
        UpdateAsync(machines => machines.TryGetValue(id, out var record)
            ? (machines.SetItem(id, record with { LastSeen = at }), record)
            : (machines, null!));

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

            var decided = record with { Status = status, DecidedAt = _time.GetUtcNow() };
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
                JsonFile.Write(_path, next.Values.OrderBy(m => m.Id).ToList());
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
