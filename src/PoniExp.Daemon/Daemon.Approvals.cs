using System.Text.Json;
using Microsoft.Extensions.Logging;
using PoniExp.Client;
using PoniExp.Core.Crypto;
using PoniExp.Core.Protocol;

namespace PoniExp.Daemon;

/// <summary>
/// Approvals and the owner's desk (see docs/plan.md, Phase 9). An agent asks; its daemon answers from an "always
/// allow" rule or sends the request, encrypted, to the desk machine's daemon; the owner answers there. Only a desk
/// claim signed by the pinned owner key counts, and only the desk machine's answers are accepted.
/// </summary>
public sealed partial class Daemon
{
    private const string OwnerSetting = "owner_key";
    private const string HereSetting = "here";
    private const string HereSignedSetting = "here_signed_issued";
    private const string OwnerHistorySetting = "owner_history";
    private const int MaxSummary = 2000;
    private const int MaxDetails = 64 * 1024;

    private readonly Lock _deskLock = new();
    /// <summary>Asking and withdrawing one at a time, so a withdrawal that overtakes its request still wins.</summary>
    private readonly SemaphoreSlim _approvalGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _withdrawnEarly = new();
    private TaskCompletionSource _approvalsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _routerOwner;

    /// <summary>The owner key pinned on this machine: only its signatures count.</summary>
    internal OwnerKey? PinnedOwner => Store.GetSetting(OwnerSetting) is { } line ? OwnerKey.Parse(line) : null;

    private HereClaim? Here => Store.GetSetting(HereSetting) is { } json ? JsonSerializer.Deserialize<HereClaim>(json, WireProtocol.Json) : null;

    /// <summary>The desk machine now, or null.</summary>
    internal string? Desk => Here?.DeskAt(Now);

    // ---- the owner and the desk ----

    private async Task SyncDeskAsync()
    {
        _routerOwner = await _relay.GetOwnerAsync();
        FollowHandovers(await _relay.GetOwnerHandoversAsync());
        if (await _relay.GetHereAsync() is { } claim)
        {
            await ApplyHereAsync(claim);
        }
    }

    internal async Task<LocalOwner> OwnerAsync(CancellationToken cancellationToken)
    {
        if (_relay.IsConnected)
        {
            _routerOwner = await _relay.GetOwnerAsync(cancellationToken);
        }

        var router = _routerOwner is { } line ? OwnerKey.Parse(line) : null;
        var pinned = PinnedOwner;
        var here = Here;
        var desk = here?.DeskAt(Now);
        return new LocalOwner(MachineId, _relay.RouterId, router?.ToString(), router?.Fingerprint, pinned?.ToString(), pinned?.Fingerprint, desk,
            desk is null ? null : desk == MachineId ? OwnName : NameOf(desk), desk is null ? null : here!.ExpiresOn, desk == MachineId,
            _options.CurrentValue.HereLifetime);
    }

    /// <summary>Pins the router's owner key (the caller showed its fingerprint and had a yes, or the local SSH agent holds it).</summary>
    internal async Task<LocalOwner> PinOwnerAsync(string key, CancellationToken cancellationToken)
    {
        var owner = OwnerKey.Parse(key);
        if (_relay.IsConnected && await _relay.GetOwnerAsync(cancellationToken) is { } router && !OwnerKey.Parse(router).SameKeyAs(owner))
        {
            throw new ArgumentException("that isn't the router's owner key (poniexp-client owner shows it)");
        }

        Store.Transact(Now, tx => tx.SetSetting(OwnerSetting, owner.ToString()));
        Diagnose("info", "owner", $"owner key pinned: {owner.Fingerprint} {owner.Comment}");
        if (_relay.IsConnected && await _relay.GetHereAsync(cancellationToken) is { } claim)
        {
            await ApplyHereAsync(claim);
        }

        return await OwnerAsync(cancellationToken);
    }

    /// <summary>Makes this machine the desk: <paramref name="claim"/> was signed by the owner (the CLI had the SSH agent sign it).</summary>
    internal async Task<LocalOwner> HereAsync(HereClaim claim, CancellationToken cancellationToken)
    {
        var owner = PinnedOwner ?? throw new InvalidOperationException("no owner key is pinned here (poniexp-client owner pin)");
        if (claim.MachineId != MachineId || claim.Problem(owner) is { } problem)
        {
            throw new ArgumentException(claim.MachineId != MachineId ? "that desk claim names another machine" : $"refused: {claim.Problem(owner)}");
        }

        await ApplyHereAsync(await _relay.SetHereAsync(claim, cancellationToken));
        return await OwnerAsync(cancellationToken);
    }

    /// <summary>Ends the desk: from the desk machine without a signature, elsewhere with an owner-signed away.</summary>
    internal async Task<LocalOwner> AwayAsync(HereClaim? signed, CancellationToken cancellationToken)
    {
        if (signed is not null)
        {
            var owner = PinnedOwner ?? throw new InvalidOperationException("no owner key is pinned here (poniexp-client owner pin)");
            if (signed.MachineId is not null || signed.Problem(owner) is { } problem)
            {
                throw new ArgumentException(signed.MachineId is not null ? "that isn't an away claim" : $"refused: {signed.Problem(owner)}");
            }

            await ApplyHereAsync(await _relay.SetHereAsync(signed, cancellationToken));
        }
        else if (Desk == MachineId)
        {
            await _relay.LeaveDeskAsync(cancellationToken);
            if (await _relay.GetHereAsync(cancellationToken) is { } claim)
            {
                await ApplyHereAsync(claim);
            }
        }
        else
        {
            throw new InvalidOperationException("this machine isn't the desk; ending the desk from here needs the owner's signature");
        }

        return await OwnerAsync(cancellationToken);
    }

    /// <summary>
    /// A desk claim from the router: accepted if signed by the pinned owner key and newer than the last signed one, or if
    /// it's the router's unsigned "the desk left" (at worst that stops approvals; it can never name a desk).
    /// </summary>
    private async Task ApplyHereAsync(HereClaim claim)
    {
        if (ApplyHere(claim) is ({ } before, var after) && before.Desk != after)
        {
            await OnDeskMovedAsync(before.Desk, after);
        }
    }

    /// <summary>On the relay's worker, so the desk is known before any approval message the router sends after it.</summary>
    private void OnHereChanged(HereClaim claim)
    {
        if (ApplyHere(claim) is ({ } before, var after) && before.Desk != after)
        {
            Background(() => OnDeskMovedAsync(before.Desk, after));
        }
    }

    private sealed record DeskBefore(string? Desk);

    /// <summary>Null if the claim wasn't taken; else the desk before and after.</summary>
    private (DeskBefore, string?)? ApplyHere(HereClaim claim)
    {
        string? before, after;
        lock (_deskLock)
        {
            var owner = PinnedOwner;
            var signedBefore = Store.GetSetting(HereSignedSetting) is { } s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
            before = Desk;
            var unsignedAway = claim.MachineId is null && claim.Signature.Length == 0;
            if (!unsignedAway)
            {
                if (owner is null)
                {
                    Diagnose("info", "owner", "the router sent a desk claim, but no owner key is pinned here (poniexp-client owner pin)");
                    return null;
                }

                if (claim.Problem(owner) is { } problem)
                {
                    Diagnose("warning", "owner", $"refused a desk claim from the router: {problem}");
                    return null;
                }

                if (signedBefore is { } last && claim.IssuedOn <= last)
                {
                    return null; // already have it, or older
                }
            }
            else if (before is null)
            {
                return null;
            }

            Store.Transact(Now, tx =>
            {
                tx.SetSetting(HereSetting, JsonSerializer.Serialize(claim, WireProtocol.Json));
                if (!unsignedAway)
                {
                    tx.SetSetting(HereSignedSetting, claim.IssuedOn.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                }
            });
            after = Desk;
            _lastDesk = after;
        }

        return (new DeskBefore(before), after);
    }

    /// <summary>Pending requests follow the desk; a desk that moved on marks what waited on it as moved.</summary>
    private async Task OnDeskMovedAsync(string? before, string? after)
    {
        _logger.LogInformation("Desk: {Desk}", after is null ? "none" : after == MachineId ? "this machine" : NameOf(after));
        Store.Transact(Now, tx =>
        {
            if (before == MachineId)
            {
                foreach (var waiting in tx.GetApprovals(ApprovalRecord.In, ApprovalRecord.Pending).Concat(tx.GetApprovals(ApprovalRecord.Join, ApprovalRecord.Pending)))
                {
                    tx.SaveApproval(waiting with { State = ApprovalRecord.Moved });
                }
            }

            foreach (var mine in tx.GetApprovals(ApprovalRecord.Out, ApprovalRecord.Pending).Where(a => a.SentTo != after))
            {
                tx.SaveApproval(mine with { SentTo = after });
                if (after is not null && after != MachineId)
                {
                    ComposeApproval(tx, after, MessageType.ApprovalRequest, RequestBody(mine));
                }
            }
        });
        await FlushQuietlyAsync();
        SignalApprovals();
    }

    /// <summary>Checked now and then: a desk that ran out moves the requests too.</summary>
    private string? _lastDesk;

    private async Task CheckDeskExpiryAsync()
    {
        string? was;
        lock (_deskLock)
        {
            was = _lastDesk;
            if (was is null || Desk is not null)
            {
                return;
            }

            _lastDesk = null;
        }

        await OnDeskMovedAsync(was, null);
    }

    /// <summary>
    /// Follows owner-key handovers from this machine's pin: each one signed by the key before it. Earlier keys are
    /// remembered (groups they signed still count).
    /// </summary>
    private void FollowHandovers(IReadOnlyList<AdminCommand> handovers)
    {
        lock (_deskLock)
        {
            if (PinnedOwner is not { } pinned)
            {
                return;
            }

            var current = pinned;
            var history = OwnerHistory.ToList();
            for (var moved = true; moved;)
            {
                moved = false;
                foreach (var handover in handovers)
                {
                    OwnerKey next;
                    try
                    {
                        next = OwnerKey.Parse(handover.NewOwnerKey ?? "");
                    }
                    catch (FormatException)
                    {
                        continue;
                    }

                    if (!next.SameKeyAs(current) && !history.Any(h => h.SameKeyAs(next)) && handover.IsSignedBy(current))
                    {
                        history.Add(current);
                        current = next;
                        moved = true;
                    }
                }
            }

            if (!current.SameKeyAs(pinned))
            {
                Store.Transact(Now, tx =>
                {
                    tx.SetSetting(OwnerSetting, current.ToString());
                    tx.SetSetting(OwnerHistorySetting, JsonSerializer.Serialize(history.Select(h => h.ToString()), WireProtocol.Json));
                });
                Diagnose("info", "owner", $"the owner key was handed over (signed by the key pinned here): now {current.Fingerprint} {current.Comment}");
            }
        }
    }

    /// <summary>Owner keys this machine followed a handover from.</summary>
    private IReadOnlyList<OwnerKey> OwnerHistory =>
        Store.GetSetting(OwnerHistorySetting) is { } json ? [.. JsonSerializer.Deserialize<List<string>>(json, WireProtocol.Json)!.Select(OwnerKey.Parse)] : [];

    /// <summary>A group counts if the owner signed it (once an owner key is pinned here).</summary>
    internal string? GroupProblem(GroupInfo group)
    {
        if (PinnedOwner is not { } owner)
        {
            return null;
        }

        if (group.Signed is not { GroupSet: { } set } signed || set.Name != group.Name || !set.Members.Distinct().SequenceEqual(group.Members)
            || !(signed.IsSignedBy(owner) || OwnerHistory.Any(signed.IsSignedBy)))
        {
            return $"group '{group.Name}' isn't signed by the owner (set it with poniexp-client admin group set)";
        }

        return null;
    }

    internal Task<AdminResult> RunAdminAsync(AdminCommand command, CancellationToken cancellationToken) => _relay.RunAdminAsync(command, cancellationToken);

    // ---- join requests on the desk ----

    /// <summary>The router's list of machines waiting to join, sent to the desk: kept as "join" approvals.</summary>
    private void OnPendingJoins(IReadOnlyList<PendingJoin> joins) => Background(async () =>
    {
        if (Desk != MachineId)
        {
            return;
        }

        var waiting = joins.ToDictionary(j => JoinId(j.MachineId));
        var gone = Store.GetApprovals(ApprovalRecord.Join, ApprovalRecord.Pending).Where(a => !waiting.ContainsKey(a.Id)).ToList();
        if (gone.Count > 0)
        {
            await _relay.RefreshMachinesAsync();
        }

        Store.Transact(Now, tx =>
        {
            foreach (var (id, join) in waiting)
            {
                if (tx.GetApproval(id) is not { State: ApprovalRecord.Pending })
                {
                    tx.SaveApproval(new ApprovalRecord(id, ApprovalRecord.Join, join.MachineId, join.Name, $"Machine '{join.Name}' asks to join",
                        $"join code {join.JoinCode}\n{join.OperatingSystem}, host {join.HostName}{(join.Notes is { Length: > 0 } n ? $"\nnotes: {n}" : "")}",
                        "join", join.JoinCode, join.RequestedOn, ApprovalRecord.Pending, MachineId));
                }
            }

            foreach (var answered in gone)
            {
                tx.SaveApproval(_relay.Machine(answered.Machine) is not null
                    ? answered with { State = ApprovalRecord.Approved, AnsweredOn = Now, AnsweredBy = "admin" }
                    : answered with { State = ApprovalRecord.Withdrawn, AnsweredOn = Now, Reason = "no longer waiting (removed, or it gave up)" });
            }
        });
        SignalApprovals();
    });

    private static Guid JoinId(string machineId) => Guid.TryParseExact(machineId, "N", out var id) ? id : Guid.Empty;

    // ---- requests (the asking side) ----

    internal async Task<LocalApproval> RequestApprovalAsync(string agent, string summary, string? details, string? tool, string? command, Guid? id = null)
    {
        await _approvalGate.WaitAsync();
        try
        {
            return await RequestApprovalLockedAsync(agent, summary, details, tool, command, id);
        }
        finally
        {
            _approvalGate.Release();
        }
    }

    private async Task<LocalApproval> RequestApprovalLockedAsync(string agent, string summary, string? details, string? tool, string? command, Guid? id)
    {
        if (id is { } given && Store.GetApproval(given) is { } existing)
        {
            return existing.Direction == ApprovalRecord.Out && existing.Agent == agent ? ToLocalApproval(existing)
                : throw new ArgumentException("that approval id is taken");
        }

        if (string.IsNullOrWhiteSpace(summary) || summary.Length > MaxSummary || details?.Length > MaxDetails || tool?.Length > 200 || command?.Length > MaxDetails)
        {
            throw new ArgumentException($"an approval needs a summary (at most {MaxSummary} characters; details at most 64 KB)");
        }

        var request = new ApprovalRecord(id is { } chosen && chosen != Guid.Empty ? chosen : Guid.NewGuid(), ApprovalRecord.Out, MachineId, agent, summary, details, tool, command, Now, ApprovalRecord.Pending);
        if (Store.GetApprovalRules().FirstOrDefault(r => r.Matches(agent, tool, request.Subject)) is { } rule)
        {
            var allowed = request with { State = ApprovalRecord.Approved, AnsweredOn = Now, AnsweredBy = "rule", Reason = $"rule {rule.Id.ToString()[..8]}" };
            Store.Transact(Now, tx => tx.SaveApproval(allowed));
            return ToLocalApproval(allowed);
        }

        if (_withdrawnEarly.TryRemove(request.Id, out _))
        {
            Store.Transact(Now, tx => tx.SaveApproval(request with { State = ApprovalRecord.Withdrawn, AnsweredOn = Now }));
            return ToLocalApproval(Store.GetApproval(request.Id)!);
        }

        var desk = Desk;
        Store.Transact(Now, tx =>
        {
            tx.SaveApproval(request with { SentTo = desk });
            if (desk is not null && desk != MachineId)
            {
                ComposeApproval(tx, desk, MessageType.ApprovalRequest, RequestBody(request));
            }
        });
        await FlushQuietlyAsync();
        SignalApprovals();
        return ToLocalApproval(Store.GetApproval(request.Id)!);
    }

    /// <summary>The approval once it's no longer pending, or after <paramref name="wait"/>.</summary>
    internal async Task<LocalApproval> WaitApprovalAsync(Guid id, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = Now + wait;
        while (true)
        {
            var approval = Store.GetApproval(id) ?? throw new KeyNotFoundException($"no approval {id}");
            if (approval.State != ApprovalRecord.Pending || Now >= deadline)
            {
                return ToLocalApproval(approval);
            }

            var changed = _approvalsChanged.Task;
            await Task.WhenAny(changed, Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The agent gave up: the desk is told, so it shows withdrawn.</summary>
    internal async Task WithdrawApprovalAsync(Guid id)
    {
        await _approvalGate.WaitAsync();
        try
        {
            foreach (var old in _withdrawnEarly.Where(w => Now - w.Value > TimeSpan.FromHours(1)))
            {
                _withdrawnEarly.TryRemove(old.Key, out _);
            }

            if (Store.GetApproval(id) is null)
            {
                _withdrawnEarly[id] = Now; // its request hasn't arrived yet
                return;
            }

            WithdrawLocked(id);
        }
        finally
        {
            _approvalGate.Release();
        }

        await FlushQuietlyAsync();
        SignalApprovals();
    }

    private void WithdrawLocked(Guid id) =>
        Store.Transact(Now, tx =>
        {
            if (tx.GetApproval(id) is not { Direction: ApprovalRecord.Out, State: ApprovalRecord.Pending } approval)
            {
                return;
            }

            tx.SaveApproval(approval with { State = ApprovalRecord.Withdrawn, AnsweredOn = Now });
            if (approval.SentTo is { } desk && desk != MachineId)
            {
                ComposeApproval(tx, desk, MessageType.ApprovalWithdrawn, JsonSerializer.Serialize(new ApprovalWithdrawnBody(id), WireProtocol.Json));
            }
        });

    internal IReadOnlyList<LocalApproval> Approvals() => [.. Store.GetApprovals(ApprovalRecord.Out).Select(ToLocalApproval)];

    internal IReadOnlyList<ApprovalRule> ApprovalRules() => Store.GetApprovalRules();

    internal void RemoveApprovalRule(string id)
    {
        var rule = Store.GetApprovalRules().Where(r => r.Id.ToString().StartsWith(id, StringComparison.Ordinal)).ToList() switch
        {
            [var one] when id.Length >= 4 || Guid.TryParse(id, out _) => one,
            [] or [_] => throw new KeyNotFoundException($"no rule {id}"),
            var many => throw new ArgumentException($"'{id}' matches {many.Count} rules; use more of its id"),
        };
        Store.Transact(Now, tx => tx.RemoveApprovalRule(rule.Id));
    }

    // ---- the desk (the answering side) ----

    /// <summary>What waits on this desk: other machines' requests, and this machine's own.</summary>
    internal IReadOnlyList<LocalApproval> DeskApprovals() =>
        Desk != MachineId ? [] : [.. Store.GetApprovals(state: ApprovalRecord.Pending)
            .Where(a => a.Direction is ApprovalRecord.In or ApprovalRecord.Join || a.SentTo == MachineId).OrderBy(a => a.RequestedOn).Select(ToLocalApproval)];

    internal async Task<LocalApproval> AnswerAsync(string id, bool approve, bool always, string? reason)
    {
        if (Desk != MachineId)
        {
            throw new InvalidOperationException("this machine isn't the desk (poniexp-client here)");
        }

        if (reason?.Length > MaxSummary)
        {
            throw new ArgumentException($"a reason is at most {MaxSummary} characters");
        }

        var waiting = Store.GetApprovals(state: ApprovalRecord.Pending).Where(a => (a.Direction is ApprovalRecord.In or ApprovalRecord.Join || a.SentTo == MachineId)
            && (a.Id.ToString() == id || (id.Length >= 4 && a.Id.ToString().StartsWith(id, StringComparison.Ordinal)))).ToList();
        var approval = waiting switch
        {
            [var one] => one,
            [] => throw new KeyNotFoundException($"nothing waits as {id} (poniexp-client approvals)"),
            _ => throw new ArgumentException($"'{id}' matches {waiting.Count} requests; use more of its id"),
        };

        if (approval.Direction == ApprovalRecord.Join)
        {
            throw new InvalidOperationException("a join is answered with the owner key: poniexp-client approve <id> (it signs and runs admin approve)");
        }

        var answer = new ApprovalAnswerBody(approval.Id, approve, approve && always, reason);
        Store.Transact(Now, tx =>
        {
            if (approval.Direction == ApprovalRecord.In)
            {
                tx.SaveApproval(Answered(approval, answer, OwnName));
                ComposeApproval(tx, approval.Machine, MessageType.ApprovalAnswer, JsonSerializer.Serialize(answer, WireProtocol.Json));
            }
            else
            {
                ApplyAnswer(tx, approval, answer, OwnName);
            }
        });
        await FlushQuietlyAsync();
        SignalApprovals();
        return ToLocalApproval(Store.GetApproval(approval.Id)!);
    }

    // ---- incoming (inside the transaction that stored the message) ----

    private void OnApprovalMessage(StoreTransaction tx, Envelope envelope, MessageContent content)
    {
        var from = envelope.FromMachine;
        switch (content.Type)
        {
            case MessageType.ApprovalRequest:
                if (Parse<ApprovalRequestBody>(content.Body) is not { } request || !Limits.IsValidAgent(request.Agent) || request.Summary is not { Length: > 0 and <= MaxSummary }
                    || request.Details?.Length > MaxDetails || request.Command?.Length > MaxDetails || request.Tool?.Length > 200)
                {
                    tx.Diagnose("warning", "approvals", "a malformed approval request", from, envelope.Id);
                }
                else if (Desk != MachineId)
                {
                    tx.Diagnose("info", "approvals", "an approval request for a desk that isn't here any more (its machine re-sends it to the desk)", from, envelope.Id);
                }
                else if (tx.GetApproval(request.RequestId) is null)
                {
                    tx.SaveApproval(new ApprovalRecord(request.RequestId, ApprovalRecord.In, from, request.Agent, request.Summary, request.Details, request.Tool,
                        request.Command, request.RequestedOn, ApprovalRecord.Pending, MachineId));
                }

                break;

            case MessageType.ApprovalAnswer:
                if (Parse<ApprovalAnswerBody>(content.Body) is not { } answer || tx.GetApproval(answer.RequestId) is not { Direction: ApprovalRecord.Out } mine)
                {
                    tx.Diagnose("warning", "approvals", "an approval answer for no request of this machine's", from, envelope.Id);
                }
                else if (from != Desk || mine.SentTo != from)
                {
                    tx.Diagnose("warning", "approvals", "refused an approval answer from a machine that isn't the desk", from, envelope.Id);
                }
                else if (mine.State == ApprovalRecord.Pending)
                {
                    ApplyAnswer(tx, mine, answer with { Reason = answer.Reason?.Length > MaxSummary ? answer.Reason[..MaxSummary] : answer.Reason }, NameOf(from));
                }

                break;

            case MessageType.ApprovalWithdrawn:
                if (Parse<ApprovalWithdrawnBody>(content.Body) is { } withdrawn && tx.GetApproval(withdrawn.RequestId) is
                        { Direction: ApprovalRecord.In, State: ApprovalRecord.Pending } theirs && theirs.Machine == from)
                {
                    tx.SaveApproval(theirs with { State = ApprovalRecord.Withdrawn, AnsweredOn = Now });
                }

                break;
        }

        SignalApprovals();
    }

    // ---- helpers ----

    private void ApplyAnswer(StoreTransaction tx, ApprovalRecord approval, ApprovalAnswerBody answer, string by)
    {
        tx.SaveApproval(Answered(approval, answer, by));
        if (answer.Approved && answer.Always)
        {
            tx.SaveApprovalRule(new ApprovalRule(Guid.NewGuid(), approval.Agent, approval.Tool, approval.Subject, Now));
        }
    }

    private ApprovalRecord Answered(ApprovalRecord approval, ApprovalAnswerBody answer, string by) => approval with
    {
        State = answer.Approved ? ApprovalRecord.Approved : ApprovalRecord.Denied,
        Always = answer.Always,
        Reason = answer.Reason,
        AnsweredOn = Now,
        AnsweredBy = by,
    };

    private static string RequestBody(ApprovalRecord a) =>
        JsonSerializer.Serialize(new ApprovalRequestBody(a.Id, a.Agent, a.Summary, a.Details, a.Tool, a.Command, a.RequestedOn), WireProtocol.Json);

    /// <summary>Daemon to daemon, encrypted like any message; kept as long as a message may be.</summary>
    internal void ComposeApproval(StoreTransaction tx, string machine, MessageType type, string body)
    {
        if (_relay.Machine(machine) is not { } to)
        {
            tx.Diagnose("warning", "approvals", "can't reach the desk: an unknown machine", machine);
            return;
        }

        _relay.Compose(tx, to, Address.DaemonAgent, Automatic(body, type) with
        {
            FromAgent = Address.DaemonAgent,
            ExpiresIn = _messaging.CurrentValue.MaxExpiry,
        });
    }

    private void SignalApprovals() => Interlocked.Exchange(ref _approvalsChanged, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private LocalApproval ToLocalApproval(ApprovalRecord a) =>
        new(a.Id, a.Direction, a.Direction == ApprovalRecord.Join ? $"{a.Agent} (new machine)" : $"{(a.Machine == MachineId ? OwnName : NameOf(a.Machine))}/{a.Agent}", a.Summary, a.Details, a.Tool, a.Command, a.State, a.Always,
            a.Reason, a.RequestedOn, a.AnsweredOn, a.AnsweredBy, a.SentTo is null ? null : a.SentTo == MachineId ? OwnName : NameOf(a.SentTo));
}
