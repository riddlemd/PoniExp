using System.Collections.Concurrent;
using PoniExp.Client;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// The job board (see docs/plan.md, Phase 8): an open forum on the router. This daemon signs its agents' actions,
/// renews the leases of jobs its online agents hold, tells agents of jobs they may claim, and re-posts jobs its agents
/// posted if the router no longer has them.
/// </summary>
public sealed partial class Daemon
{
    /// <summary>Jobs an agent here holds (claimed or running): their leases are renewed while that agent is online.</summary>
    private readonly ConcurrentDictionary<Guid, string> _heldJobs = new();
    private DateTimeOffset _lastLeaseRenewal;

    /// <summary>What this machine has, detected once and again on each connect (job requirements are checked against it).</summary>
    internal MachineCapabilities Capabilities => _capabilities ??= CapabilityDetector.Detect(_options.CurrentValue.CapabilityTags, Now);

    private MachineCapabilities? _capabilities;

    private async Task ReportCapabilitiesAsync()
    {
        _capabilities = CapabilityDetector.Detect(_options.CurrentValue.CapabilityTags, Now);
        await _relay.ReportCapabilitiesAsync(_capabilities);
    }

    internal async Task<LocalJob> PostJobAsync(string agent, string title, string? description, IReadOnlyList<string>? requirements, bool backlog,
        CancellationToken cancellationToken)
    {
        List<JobRequirement> parsed;
        try
        {
            parsed = [.. (requirements ?? []).Select(JobRequirement.Parse)];
        }
        catch (FormatException e)
        {
            throw new ArgumentException(e.Message);
        }

        var posted = new PostedJob(Guid.NewGuid(), agent, title, description ?? "", parsed, backlog ? JobColumn.Backlog : JobColumn.Ready);
        var job = await SubmitJobAsync(PostOf(posted), cancellationToken);
        Store.Transact(Now, tx => tx.SavePostedJob(posted));
        return ToLocalJob(job);
    }

    internal async Task<LocalJob> ClaimJobAsync(string agent, string id, CancellationToken cancellationToken)
    {
        var job = await RequireJobAsync(id, cancellationToken);
        if (JobRequirement.Unmet(job.Requirements, Capabilities) is { Count: > 0 } unmet)
        {
            throw new InvalidOperationException($"this machine doesn't meet {string.Join(", ", unmet)} ({Capabilities.Summary()})");
        }

        var claimed = await SubmitJobAsync(Action(agent, job.Id, JobActionKind.Claim), cancellationToken);
        _heldJobs[claimed.Id] = agent;
        return ToLocalJob(claimed);
    }

    /// <summary>Moves a job to <paramref name="column"/>; <paramref name="note"/> is the result for done and failed.</summary>
    internal async Task<LocalJob> MoveJobAsync(string agent, string id, string column, string? note, CancellationToken cancellationToken)
    {
        var to = Enum.GetValues<JobColumn>().FirstOrDefault(c => WireProtocol.NameOf(c) == column, (JobColumn)(-1));
        if (to < 0)
        {
            throw new ArgumentException($"a column is one of {string.Join(", ", Enum.GetValues<JobColumn>().Select(c => WireProtocol.NameOf(c)))}");
        }

        var job = await RequireJobAsync(id, cancellationToken);
        var moved = await SubmitJobAsync(Action(agent, job.Id, JobActionKind.Move, to, note), cancellationToken);
        if (moved.IsFinished)
        {
            _heldJobs.TryRemove(moved.Id, out _);
        }

        return ToLocalJob(moved);
    }

    /// <summary>Release, cancel or comment.</summary>
    internal async Task<LocalJob> JobActionAsync(string agent, string id, JobActionKind kind, string? text, CancellationToken cancellationToken)
    {
        var job = await RequireJobAsync(id, cancellationToken);
        var changed = await SubmitJobAsync(Action(agent, job.Id, kind, null, text), cancellationToken);
        if (kind is JobActionKind.Release or JobActionKind.Cancel)
        {
            _heldJobs.TryRemove(changed.Id, out _);
        }

        return ToLocalJob(changed);
    }

    internal async Task<IReadOnlyList<LocalJob>> JobsAsync(bool all, string? column, CancellationToken cancellationToken) =>
        [.. (await _relay.GetJobsAsync(all, cancellationToken)).Where(j => column is null || WireProtocol.NameOf(j.Column) == column).Select(ToLocalJob)];

    internal async Task<LocalJob> JobAsync(string id, CancellationToken cancellationToken) => ToLocalJob(await RequireJobAsync(id, cancellationToken));

    // ---- from the router ----

    /// <summary>A job changed: keep track of held ones, close posted ones that finished, and tell local agents.</summary>
    private void OnJobChanged(JobChange change)
    {
        var job = change.Job;
        if (job.Claimant is { } claimant && Address.Parse(claimant) is { } holder && holder.Machine == MachineId && !job.IsFinished && !change.Removed)
        {
            _heldJobs[job.Id] = holder.Agent!;
        }
        else
        {
            _heldJobs.TryRemove(job.Id, out _);
        }

        var poster = Address.Parse(job.Poster);
        if (poster.Machine == MachineId && (job.IsFinished || change.Removed))
        {
            Store.Transact(Now, tx => tx.ClosePostedJob(job.Id));
        }

        if (change.Removed)
        {
            return;
        }

        var local = ToLocalJob(job);
        var last = job.History.LastOrDefault();
        if (change.BecameReady && local.Unmet.Count == 0) // only machines that may claim it hear of it
        {
            foreach (var session in _sessions.All)
            {
                session.Push(new LocalEvent(LocalEventKinds.JobReady, Job: local));
            }
        }

        // The poster and the claimant hear of changes they didn't make themselves.
        var concerned = new[] { job.Poster, job.Claimant }.OfType<string>().Distinct().Select(Address.Parse)
            .Where(a => a.Machine == MachineId && $"{a.Machine}/{a.Agent}" != last?.Actor);
        foreach (var agent in concerned)
        {
            _sessions.Get(agent.Agent!)?.Push(new LocalEvent(LocalEventKinds.JobChanged, Job: local));
        }
    }

    /// <summary>After a (re)connect: which jobs we hold, and re-post our open jobs the router no longer has.</summary>
    private async Task SyncJobsAsync()
    {
        foreach (var job in await _relay.GetJobsAsync(includeFinished: false))
        {
            if (job.Claimant is { } claimant && Address.Parse(claimant) is { } holder && holder.Machine == MachineId)
            {
                _heldJobs[job.Id] = holder.Agent!;
            }
        }

        foreach (var posted in Store.GetOpenPostedJobs())
        {
            var job = await _relay.GetJobAsync(posted.Id);
            if (job is null)
            {
                await _relay.SubmitJobActionAsync(_relay.SignJobAction(PostOf(posted)));
                Diagnose("info", "jobs", $"re-posted job {posted.Id.ToString()[..8]} ({posted.Title}): the router didn't have it");
            }
            else if (job.IsFinished)
            {
                Store.Transact(Now, tx => tx.ClosePostedJob(posted.Id));
            }
        }
    }

    /// <summary>Renews the leases of jobs held by agents that are online (an offline agent's claim runs out).</summary>
    private async Task RenewJobLeasesAsync(CancellationToken cancellationToken)
    {
        if (Now - _lastLeaseRenewal < _options.CurrentValue.JobLeaseRenewal || _heldJobs.IsEmpty || !_relay.IsConnected)
        {
            return;
        }

        _lastLeaseRenewal = Now;
        var ids = _heldJobs.Where(h => _sessions.IsOnline(h.Value)).Select(h => h.Key).ToList();
        if (ids.Count > 0)
        {
            var held = (await _relay.RenewJobLeasesAsync(ids, cancellationToken)).ToHashSet();
            foreach (var lost in ids.Where(id => !held.Contains(id)))
            {
                _heldJobs.TryRemove(lost, out _);
            }
        }
    }

    // ---- helpers ----


    private JobAction PostOf(PostedJob posted) =>
        new(Guid.NewGuid(), posted.Id, JobActionKind.Post, Me(posted.Agent), posted.Column, posted.Title, posted.Description, posted.Requirements, Now, "", "");

    private JobAction Action(string agent, Guid job, JobActionKind kind, JobColumn? column = null, string? text = null) =>
        new(Guid.NewGuid(), job, kind, Me(agent), column, null, text, null, Now, "", "");

    private Task<Job> SubmitJobAsync(JobAction action, CancellationToken cancellationToken) =>
        _relay.SubmitJobActionAsync(_relay.SignJobAction(action), cancellationToken);

    /// <summary>A job by id or id prefix.</summary>
    private async Task<Job> RequireJobAsync(string id, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(id, out var exact))
        {
            return await _relay.GetJobAsync(exact, cancellationToken) ?? throw new KeyNotFoundException($"no job {id} on the board");
        }

        var matches = (await _relay.GetJobsAsync(includeFinished: true, cancellationToken)).Where(j => j.Id.ToString().StartsWith(id, StringComparison.Ordinal)).ToList();
        return matches switch
        {
            [var one] => one,
            [] => throw new KeyNotFoundException($"no job {id} on the board"),
            _ => throw new ArgumentException($"'{id}' matches {matches.Count} jobs; use more of its id"),
        };
    }

    private LocalJob ToLocalJob(Job j) =>
        new(j.Id, j.Title, j.Description, [.. j.Requirements.Select(r => r.ToString())],
            [.. JobRequirement.Unmet(j.Requirements, Capabilities).Select(r => r.ToString())], Display(j.Poster), WireProtocol.NameOf(j.Column), j.Claimant is { } c ? Display(c) : null, j.LeaseExpiresOn,
            j.Result, j.PostedOn, j.UpdatedOn,
            [.. j.History.Select(h => new LocalJobEntry(h.OccurredOn, h.Actor == JobRules.Router ? JobRules.Router : Display(h.Actor), h.What,
                h.Column is { } col ? WireProtocol.NameOf(col) : null, h.Text))]);
}
