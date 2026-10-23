namespace PoniExp.Client.Completion;

/// <summary>Completion candidates from the running daemon. Ids are full, so a completed word works where a prefix wouldn't.</summary>
public sealed class LocalApiCompletionSource(LocalApiClient api) : ICompletionSource
{
    private const int RecentMessages = 50;

    public async Task<IReadOnlyList<string>> ListAsync(CompletionKind kind, string? agent, CancellationToken ct) => kind switch
    {
        CompletionKind.PendingApproval => [.. (await api.DeskAsync(ct)).Select(a => a.Id.ToString())],
        CompletionKind.InboxMessage => await PerAgentAsync(agent, async a => (await api.InboxAsync(a, ct)).Select(m => m.Id.ToString()), ct),
        CompletionKind.LoggedMessage => [.. (await api.LogAsync(new LocalLogQuery(Agent: agent), ct)).TakeLast(RecentMessages).Reverse().Select(m => m.Id.ToString())],
        CompletionKind.Conversation => await PerAgentAsync(agent, async a => (await api.ConversationsAsync(a, ct)).Select(c => c.Id.ToString()), ct),
        CompletionKind.Rule => [.. (await api.ApprovalRulesAsync(ct)).Select(r => r.Id.ToString())],
        CompletionKind.Job => [.. (await api.JobsAsync(ct: ct)).Select(j => j.Id.ToString())],
        CompletionKind.Room => [.. (await api.RoomsAsync(agent, ct)).Select(r => r.Name)],
        CompletionKind.LocalAgent => await LocalAgentsAsync(ct),
        CompletionKind.WakeAgent => [.. (await api.WakeTargetsAsync(ct)).Select(w => w.Agent)],
        CompletionKind.Machine => [.. (await api.MachinesAsync(ct)).Select(m => m.Name)],
        CompletionKind.Address => await AddressesAsync(ct),
        CompletionKind.Blob => [.. (await api.BlobsAsync(ct)).Select(b => b.BlobId.ToString())],
        _ => [],
    };

    private async Task<IReadOnlyList<string>> LocalAgentsAsync(CancellationToken ct) =>
        [.. (await api.AgentsAsync(ct)).Where(a => a.RetiredOn is null).Select(a => a.Name)];

    private async Task<IReadOnlyList<string>> PerAgentAsync(string? agent, Func<string, Task<IEnumerable<string>>> list, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var a in agent is null ? await LocalAgentsAsync(ct) : [agent])
        {
            ids.AddRange(await list(a));
        }

        return ids;
    }

    private async Task<IReadOnlyList<string>> AddressesAsync(CancellationToken ct)
    {
        var machines = await api.MachinesAsync(ct);
        var rooms = await api.RoomsAsync(null, ct);
        return [.. machines.Select(m => m.Name), .. machines.SelectMany(m => m.Agents.Select(a => $"{m.Name}/{a.Name}")), .. rooms.Select(r => $"room:{r.Name}")];
    }
}
