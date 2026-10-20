using System.Security.Cryptography;
using PoniExp.Core.Crypto;

namespace PoniExp.Core.Protocol;

public enum JobColumn
{
    Backlog,
    Ready,
    Claimed,
    Running,
    Done,
    Failed,
}

public enum JobActionKind
{
    /// <summary>Opens a job (in <c>backlog</c> or <c>ready</c>), or re-posts one the router lost, under the same id.</summary>
    Post,
    /// <summary>Moves a job: anyone between backlog and ready; the claimant on to running, done or failed.</summary>
    Move,
    Claim,
    /// <summary>The claimant gives the job back: it is ready again.</summary>
    Release,
    Comment,
    /// <summary>The poster ends an unfinished job: failed, "cancelled by the poster".</summary>
    Cancel,
}

/// <summary>
/// A change to the job board, signed by the acting machine over every field. <see cref="ActorKey"/> is its key (its
/// fingerprint is the machine id in <see cref="Actor"/>, <c>machineId/agent</c>). <see cref="Text"/> is the description
/// (post), the result note (move to done or failed) or the comment.
/// </summary>
public sealed record JobAction(Guid ActionId, Guid JobId, JobActionKind Kind, string Actor, JobColumn? Column, string? Title, string? Text,
    IReadOnlyList<JobRequirement>? Requirements, DateTimeOffset OccurredOn, string ActorKey, string Signature);

/// <summary>One line in a job's history: who did what, when. Router entries (a lease running out) have actor <c>router</c>.</summary>
public sealed record JobEntry(DateTimeOffset OccurredOn, string Actor, string What, JobColumn? Column = null, string? Text = null);

/// <summary>
/// A job as the router keeps it: in the clear (the board is an open forum for approved machines). Poster and claimant are
/// <c>machineId/agent</c>.
/// </summary>
public sealed record Job(Guid Id, string Title, string Description, IReadOnlyList<JobRequirement> Requirements, string Poster, JobColumn Column, string? Claimant,
    DateTimeOffset? LeaseExpiresOn, string? Result, DateTimeOffset PostedOn, DateTimeOffset UpdatedOn, IReadOnlyList<JobEntry> History, bool PosterRevoked = false)
{
    public bool IsFinished => Column is JobColumn.Done or JobColumn.Failed;
}

/// <summary>What the router pushes to every approved machine when a job changes.</summary>
public sealed record JobChange(Job Job, bool BecameReady, bool Removed = false);

public sealed class JobRuleException(string message) : Exception(message);

/// <summary>The job board's rules, applied by the router to each signed action.</summary>
public static class JobRules
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionBytes = 64 * 1024;
    public const int MaxTextBytes = 16 * 1024;
    public const string Router = "router";

    public static bool IsSignedBy(JobAction a, string machineId)
    {
        try
        {
            return KeyFingerprint.MachineIdOf(a.ActorKey) == machineId && PublicKeys.Verify(a.ActorKey, Signatures.JobActionPayload(a), a.Signature);
        }
        catch (Exception x) when (x is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The job after <paramref name="a"/>, or why it isn't allowed.</summary>
    /// <param name="current">The job, or null for a post.</param>
    /// <param name="lease">How long a claim lasts unless renewed.</param>
    /// <param name="claimant">What the acting machine reported it has (checked against a claimed job's requirements).</param>
    public static Job Apply(Job? current, JobAction a, DateTimeOffset now, TimeSpan lease, MachineCapabilities? claimant = null)
    {
        static JobRuleException No(string why) => new(why);
        if (System.Text.Encoding.UTF8.GetByteCount(a.Text ?? "") > (a.Kind == JobActionKind.Post ? MaxDescriptionBytes : MaxTextBytes))
        {
            throw No(a.Kind == JobActionKind.Post ? "the description is at most 64 KB" : "the text is at most 16 KB");
        }

        if (a.Kind == JobActionKind.Post)
        {
            if (current is not null)
            {
                throw No("there's already a job with that id");
            }

            if (a.Title is not { Length: > 0 and <= MaxTitleLength } title || title.Any(char.IsControl))
            {
                throw No($"a job needs a title of 1-{MaxTitleLength} characters, on one line");
            }

            if (a.Requirements is { Count: > JobRequirement.MaxPerJob })
            {
                throw No($"a job has at most {JobRequirement.MaxPerJob} requirements");
            }

            var requirements = new List<JobRequirement>();
            foreach (var requirement in a.Requirements ?? [])
            {
                try
                {
                    requirements.Add(JobRequirement.Parse(requirement.ToString()));
                }
                catch (FormatException e)
                {
                    throw No(e.Message);
                }
            }

            if (a.Column is not (null or JobColumn.Backlog or JobColumn.Ready))
            {
                throw No("a job is posted to backlog or ready");
            }

            var column = a.Column ?? JobColumn.Ready;
            return new Job(a.JobId, title, a.Text ?? "", requirements, a.Actor, column, null, null, null, now, now,
                [new JobEntry(a.OccurredOn, a.Actor, "posted", column)]);
        }

        if (current is null)
        {
            throw No("no such job");
        }

        var job = current;
        if (job.IsFinished && a.Kind != JobActionKind.Comment)
        {
            throw No($"the job is {WireProtocol.NameOf(job.Column)}");
        }

        var isClaimant = job.Claimant == a.Actor;
        Job Next(JobColumn column, string what, string? claimant, DateTimeOffset? leaseUntil, string? result = null) =>
            job with
            {
                Column = column,
                Claimant = claimant,
                LeaseExpiresOn = leaseUntil,
                Result = result ?? job.Result,
                UpdatedOn = now,
                History = [.. job.History, new JobEntry(a.OccurredOn, a.Actor, what, column, a.Text)],
            };

        switch (a.Kind)
        {
            case JobActionKind.Comment:
                if (string.IsNullOrWhiteSpace(a.Text))
                {
                    throw No("a comment needs text");
                }

                return job with { UpdatedOn = now, History = [.. job.History, new JobEntry(a.OccurredOn, a.Actor, "commented", null, a.Text)] };
            case JobActionKind.Claim:
                if (job.Column == JobColumn.Ready && JobRequirement.Unmet(job.Requirements, claimant) is { Count: > 0 } unmet)
                {
                    throw No(claimant is null ? "this machine hasn't reported what it has, so it meets no requirements"
                        : $"this machine doesn't meet {string.Join(", ", unmet)}");
                }

                return job.Column == JobColumn.Ready ? Next(JobColumn.Claimed, "claimed", a.Actor, now + lease)
                    : throw No(job.Claimant is { } c ? $"the job is already claimed by {c}" : $"only a ready job can be claimed (it's {WireProtocol.NameOf(job.Column)})");
            case JobActionKind.Release:
                return isClaimant ? Next(JobColumn.Ready, "released", null, null) : throw No("only the claimant can release the job");
            case JobActionKind.Cancel:
                return job.Poster == a.Actor ? Next(JobColumn.Failed, "cancelled", null, null, "cancelled by the poster")
                    : job.PosterRevoked ? Next(JobColumn.Failed, "cancelled", null, null, "cancelled (its poster's machine was revoked)")
                    : throw No("only the poster can cancel the job");
            case JobActionKind.Move:
                switch (a.Column)
                {
                    case JobColumn.Backlog or JobColumn.Ready when job.Column is JobColumn.Backlog or JobColumn.Ready:
                        return job.Column == a.Column ? throw No($"the job is already {WireProtocol.NameOf(job.Column)}") : Next(a.Column.Value, "moved", null, null);
                    case JobColumn.Running when job.Column == JobColumn.Claimed:
                        return isClaimant ? Next(JobColumn.Running, "started", a.Actor, job.LeaseExpiresOn) : throw No("only the claimant moves a claimed job on");
                    case JobColumn.Done or JobColumn.Failed when job.Column is JobColumn.Claimed or JobColumn.Running:
                        return isClaimant ? Next(a.Column.Value, a.Column == JobColumn.Done ? "finished" : "failed", a.Actor, null, a.Text ?? "")
                            : throw No("only the claimant moves a claimed job on");
                    default:
                        throw No($"a job can't move from {WireProtocol.NameOf(job.Column)} to {(a.Column is { } to ? WireProtocol.NameOf(to) : "nowhere")}" +
                                 (job.Column == JobColumn.Ready && a.Column is JobColumn.Claimed ? " (claim it instead)" : ""));
                }

            default:
                throw No("that isn't a job action");
        }
    }

    /// <summary>A claim whose lease ran out: back to ready.</summary>
    /// <summary>A machine was revoked (phase 11): what it held goes back to ready, what it posted is marked so anyone may cancel it.</summary>
    public static Job? MachineRevoked(Job job, string machineId, DateTimeOffset now)
    {
        static bool On(string? actor, string machine) => actor is not null && actor.StartsWith(machine + "/", StringComparison.Ordinal);
        if (job.IsFinished)
        {
            return null;
        }

        var next = job;
        if (On(job.Claimant, machineId))
        {
            next = next with
            {
                Column = JobColumn.Ready, Claimant = null, LeaseExpiresOn = null, UpdatedOn = now,
                History = [.. next.History, new JobEntry(now, Router, "its claimant's machine was revoked", JobColumn.Ready)],
            };
        }

        if (On(job.Poster, machineId) && !job.PosterRevoked)
        {
            next = next with
            {
                PosterRevoked = true, UpdatedOn = now,
                History = [.. next.History, new JobEntry(now, Router, "its poster's machine was revoked; anyone may cancel it")],
            };
        }

        return ReferenceEquals(next, job) ? null : next;
    }

    public static Job LeaseExpired(Job job, DateTimeOffset now) =>
        job with
        {
            Column = JobColumn.Ready,
            Claimant = null,
            LeaseExpiresOn = null,
            UpdatedOn = now,
            History = [.. job.History, new JobEntry(now, Router, "lease ran out", JobColumn.Ready)],
        };
}
