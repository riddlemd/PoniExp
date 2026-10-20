using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;
using PoniExp.Server.Hubs;
using PoniExp.Server.Machines;

namespace PoniExp.Server.Jobs;

/// <summary>
/// The job board (see docs/plan.md, Phase 8): an open forum for approved machines, kept in the clear in router.db.
/// Every change is a signed <see cref="JobAction"/>, checked against <see cref="JobRules"/>; claims are leases the
/// claimant's daemon renews. Changes are pushed to every approved machine.
/// </summary>
public sealed class JobBoard
{
    private readonly RouterStore _store;
    private readonly IHubContext<RelayHub> _hub;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<JobBoard> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Job> _jobs;

    public JobBoard(RouterStore store, IHubContext<RelayHub> hub, IOptionsMonitor<RouterOptions> options, TimeProvider time, ILogger<JobBoard> logger)
    {
        (_store, _hub, _options, _time, _logger) = (store, hub, options, time, logger);
        _jobs = store.LoadJobs().ToDictionary(j => j.Id);
    }

    /// <summary>Applies a signed action by the caller's machine.</summary>
    public async Task<Job> SubmitAsync(string caller, JobAction a)
    {
        Job job;
        bool becameReady;
        lock (_lock)
        {
            Address actor;
            try
            {
                actor = Address.Parse(a.Actor);
            }
            catch (FormatException)
            {
                throw new RouterException("A job action's actor is machine/agent.");
            }

            if (actor.Machine != caller || actor.Agent is null || !JobRules.IsSignedBy(a, caller))
            {
                throw new RouterException("A job action must be your own machine's, signed with its key.");
            }

            var now = _time.GetUtcNow();
            if ((a.OccurredOn - now).Duration() > _options.CurrentValue.ClockSkew)
            {
                throw new RouterException("The job action's time is too far from the router's; check this machine's clock.");
            }

            var current = _jobs.GetValueOrDefault(a.JobId);
            if (a.Kind == JobActionKind.Post && current is null
                && _jobs.Values.Count(j => !j.IsFinished && Address.Parse(j.Poster).Machine == caller) >= _options.CurrentValue.MaxLiveJobsPerMachine)
            {
                throw new RouterException($"Your machine has {_options.CurrentValue.MaxLiveJobsPerMachine} unfinished jobs on the board, the most it may have.");
            }

            try
            {
                job = JobRules.Apply(current, a, now, _options.CurrentValue.JobLease);
            }
            catch (JobRuleException e)
            {
                throw new RouterException($"Refused: {e.Message}.");
            }

            becameReady = job.Column == JobColumn.Ready && current?.Column != JobColumn.Ready;
            Save(job);
        }

        await PushAsync(new JobChange(job, becameReady));
        return job;
    }

    /// <summary>The board: unfinished jobs, and finished ones too if asked.</summary>
    public IReadOnlyList<Job> List(bool includeFinished)
    {
        lock (_lock)
        {
            return [.. _jobs.Values.Where(j => includeFinished || !j.IsFinished).OrderBy(j => j.PostedOn)];
        }
    }

    public Job? Get(Guid id)
    {
        lock (_lock)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    /// <summary>Extends the leases of the given jobs the caller's machine holds. Returns those it still holds.</summary>
    public IReadOnlyList<Guid> Renew(string caller, IReadOnlyCollection<Guid> ids)
    {
        lock (_lock)
        {
            var until = _time.GetUtcNow() + _options.CurrentValue.JobLease;
            var held = new List<Guid>();
            foreach (var id in ids)
            {
                if (_jobs.TryGetValue(id, out var job) && job.Column is JobColumn.Claimed or JobColumn.Running && job.Claimant is { } claimant
                    && Address.Parse(claimant).Machine == caller)
                {
                    _jobs[id] = job with { LeaseExpiresOn = until };
                    _store.SaveJob(_jobs[id]);
                    held.Add(id);
                }
            }

            return held;
        }
    }

    /// <summary>Returns jobs whose lease ran out to ready; drops finished jobs older than <see cref="RouterOptions.JobRetention"/>.</summary>
    public async Task SweepAsync()
    {
        var now = _time.GetUtcNow();
        var changes = new List<JobChange>();
        lock (_lock)
        {
            foreach (var job in _jobs.Values.Where(j => j.LeaseExpiresOn is { } until && until <= now && !j.IsFinished).ToList())
            {
                var back = JobRules.LeaseExpired(job, now);
                Save(back);
                changes.Add(new JobChange(back, true));
                _logger.LogInformation("Job {Id}: the lease of {Claimant} ran out", job.Id.ToString()[..8], job.Claimant);
            }

            foreach (var job in _jobs.Values.Where(j => j.IsFinished && now - j.UpdatedOn >= _options.CurrentValue.JobRetention).ToList())
            {
                _jobs.Remove(job.Id);
                _store.DeleteJob(job.Id);
                changes.Add(new JobChange(job, false, Removed: true));
            }
        }

        foreach (var change in changes)
        {
            await PushAsync(change);
        }
    }

    /// <summary>Puts back the live jobs from a backup (ones the board already has are kept as they are).</summary>
    public int Restore(IReadOnlyList<Job> jobs)
    {
        lock (_lock)
        {
            var restored = 0;
            foreach (var job in jobs.Where(j => !_jobs.ContainsKey(j.Id)))
            {
                Save(job);
                restored++;
            }

            return restored;
        }
    }

    private void Save(Job job)
    {
        _jobs[job.Id] = job;
        _store.SaveJob(job);
    }

    private Task PushAsync(JobChange change) => _hub.Clients.Group(RelayHub.MembersGroup).SendAsync(HubMethods.JobChanged, change);
}
