using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// Groups (see docs/plan.md, Phase 7), resolved here, by the sender's daemon, into plain <c>machine/agent</c> addresses:
/// the router only ever sees ordinary messages.
/// </summary>
public sealed partial class Daemon
{
    /// <summary>Expands <c>group:x</c> into its members. Plain addresses pass through. Duplicates go once.</summary>
    internal async Task<IReadOnlyList<string>> ResolveAddressesAsync(IReadOnlyCollection<string> to, string from, CancellationToken cancellationToken)
    {
        var resolved = new List<string>();
        IReadOnlyList<GroupInfo>? groups = null;
        foreach (var text in to)
        {
            if (Address.ParseIndirect(text) is { } group)
            {
                groups ??= await _relay.GetGroupsAsync(cancellationToken);
                resolved.AddRange(groups.FirstOrDefault(g => g.Name == group.Name)?.Members
                                  ?? throw new KeyNotFoundException($"no group '{group.Name}' (the owner sets groups: poniexp admin group set)"));
            }
            else
            {
                resolved.Add(text);
            }
        }

        var distinct = resolved.Distinct().ToList();
        return distinct.Count > 0 ? distinct : throw new KeyNotFoundException("those addresses resolve to no one");
    }
}
