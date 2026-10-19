using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// Roles and groups (see docs/plan.md, Phase 7), resolved here, by the sender's daemon, into plain
/// <c>machine/endpoint</c> addresses: the router only ever sees ordinary messages.
/// </summary>
public sealed partial class Daemon
{
    /// <summary>
    /// Expands <c>role:x</c> (any one holder: an online one at random, else the one seen most recently),
    /// <c>role:x/all</c> (every holder) and <c>group:x</c> (its members, roles in it expanded). Plain addresses
    /// pass through. Duplicates go once. <paramref name="from"/> is never its own recipient by role.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ResolveAddressesAsync(IReadOnlyCollection<string> to, string from, CancellationToken cancellationToken)
    {
        var resolved = new List<string>();
        IReadOnlyList<GroupInfo>? groups = null;
        foreach (var text in to)
        {
            switch (Address.ParseIndirect(text))
            {
                case null:
                    resolved.Add(text);
                    break;
                case { Kind: IndirectKind.Role } role:
                    resolved.AddRange(HoldersOf(role.Name, role.All, from));
                    break;
                case { Kind: IndirectKind.Group } group:
                    groups ??= await _relay.GetGroupsAsync(cancellationToken);
                    var members = groups.FirstOrDefault(g => g.Name == group.Name)?.Members
                                  ?? throw new KeyNotFoundException($"no group '{group.Name}' (the owner sets groups: poniexp admin group set)");
                    foreach (var member in members)
                    {
                        resolved.AddRange(Address.ParseIndirect(member) is { Kind: IndirectKind.Role } r ? HoldersOf(r.Name, r.All, from, quiet: true) : [member]);
                    }

                    break;
            }
        }

        var distinct = resolved.Distinct().ToList();
        return distinct.Count > 0 ? distinct : throw new KeyNotFoundException("those addresses resolve to no one");
    }

    /// <summary>Endpoints (here and announced by other machines) that declare <paramref name="role"/>, as <c>machine/endpoint</c>.</summary>
    private IReadOnlyList<string> HoldersOf(string role, bool all, string from, bool quiet = false)
    {
        var holders = new List<(string Address, bool Online, DateTimeOffset ChangedOn)>();
        foreach (var e in Store.GetEndpoints().Where(e => e.RetiredOn is null && e.Name != from && (e.Roles?.Contains(role) ?? false)))
        {
            holders.Add(($"{MachineId}/{e.Name}", _sessions.IsOnline(e.Name), e.ChangedOn));
        }

        var machines = _relay.Machines.ToDictionary(m => m.Id);
        foreach (var e in Store.GetPeerEndpoints().Where(e => e.Machine != MachineId && (e.Roles?.Contains(role) ?? false)))
        {
            if (machines.TryGetValue(e.Machine, out var machine))
            {
                holders.Add(($"{e.Machine}/{e.Endpoint}", machine.Online && e.State == EndpointState.Online, e.ChangedOn));
            }
        }

        if (holders.Count == 0)
        {
            return quiet ? [] : throw new KeyNotFoundException($"no endpoint declares the role '{role}'");
        }

        if (all)
        {
            return [.. holders.Select(h => h.Address)];
        }

        var online = holders.Where(h => h.Online).ToList();
        return [online.Count > 0 ? online[Random.Shared.Next(online.Count)].Address : holders.MaxBy(h => h.ChangedOn).Address];
    }
}
