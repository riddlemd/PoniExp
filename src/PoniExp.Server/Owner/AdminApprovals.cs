using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;
using PoniExp.Server.Hubs;

namespace PoniExp.Server.Owner;

/// <summary>
/// Signed-only changes (see docs/plan.md, Phase 11). Once the router has an owner key, an admin change runs only
/// signed: the in-process <see cref="Token"/> marks a signed run (RemoteAdmin passes it to the admin listener; a
/// docker exec process can't know it). A docker exec change waits here until the desk signs it, is denied, or times out.
/// </summary>
public sealed class AdminApprovals
{
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly OwnerDesk _desk;
    private readonly IHubContext<RelayHub> _hub;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminApprovals> _logger;

    public AdminApprovals(OwnerDesk desk, IHubContext<RelayHub> hub, IOptionsMonitor<RouterOptions> options, TimeProvider time, ILogger<AdminApprovals> logger)
    {
        (_desk, _hub, _options, _time, _logger) = (desk, hub, options, time, logger);
        desk.DeskMoved += () => _ = PushAsync();
    }

    public const string TokenHeader = "X-PoniExp-Signed";

    /// <summary>Marks a signed run on the admin listener.</summary>
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public sealed class Pending(AdminRequest request, string? input)
    {
        public AdminRequest Request { get; } = request;
        public string? Input { get; } = input;
        public string State { get; set; } = "pending";
        public AdminResult? Result { get; set; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A docker exec change; null if the router has no owner key (then it runs unsigned).</summary>
    public async Task<AdminRequest?> RequestAsync(IReadOnlyList<string> args, string? input)
    {
        if (_desk.Owner is null)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var id = Guid.NewGuid();
        var request = new AdminRequest(id, args, CodeOf(id), AdminCommand.HashOf(input), input?.Length ?? 0, now, now + _options.CurrentValue.AdminApprovalTimeout);
        _pending[id] = new Pending(request, input);
        _logger.LogInformation("docker exec change waiting for the desk ({Code}): {Command}", request.Code, string.Join(" ", args));
        await PushAsync();
        return request;
    }

    public Pending? Get(Guid id)
    {
        Expire();
        return _pending.GetValueOrDefault(id);
    }

    /// <summary>The state once it's no longer pending, or after <paramref name="wait"/>.</summary>
    public async Task<Pending?> WaitAsync(Guid id, TimeSpan wait)
    {
        if (Get(id) is not { } pending)
        {
            return null;
        }

        var untilExpiry = pending.Request.ExpiresOn - _time.GetUtcNow() + TimeSpan.FromMilliseconds(50);
        await Task.WhenAny(pending.Done.Task, Task.Delay(untilExpiry < wait ? (untilExpiry > TimeSpan.Zero ? untilExpiry : TimeSpan.Zero) : wait));
        return Get(id) ?? pending;
    }

    public async Task CompleteAsync(Guid id, AdminResult result)
    {
        if (_pending.TryGetValue(id, out var pending) && pending.State == "pending")
        {
            pending.Result = result;
            await FinishAsync(pending, "approved");
        }
    }

    public async Task DenyAsync(string from, Guid id)
    {
        if (_desk.Desk != from)
        {
            throw new RouterException("Only the desk answers docker exec changes.");
        }

        if (_pending.TryGetValue(id, out var pending) && pending.State == "pending")
        {
            await FinishAsync(pending, "denied");
        }
    }

    public IReadOnlyList<AdminRequest> Waiting()
    {
        Expire();
        return [.. _pending.Values.Where(p => p.State == "pending").Select(p => p.Request).OrderBy(r => r.CreatedOn)];
    }

    public async Task PushAsync()
    {
        if (_desk.Desk is { } desk)
        {
            await _hub.Clients.Group(RelayHub.MachineGroup(desk)).SendAsync(HubMethods.AdminRequestsChanged, Waiting());
        }
    }

    private async Task FinishAsync(Pending pending, string state)
    {
        pending.State = state;
        pending.Done.TrySetResult();
        _logger.LogInformation("docker exec change {Code} {State}", pending.Request.Code, state);
        await PushAsync();
    }

    private void Expire()
    {
        var now = _time.GetUtcNow();
        var changed = false;
        foreach (var (id, p) in _pending)
        {
            if (p.State == "pending" && now >= p.Request.ExpiresOn)
            {
                p.State = "timed-out";
                p.Done.TrySetResult();
                changed = true;
            }

            if (now - p.Request.ExpiresOn > TimeSpan.FromHours(1))
            {
                _pending.TryRemove(id, out _);
            }
        }

        if (changed)
        {
            _ = PushAsync();
        }
    }

    /// <summary>Six characters from the id, as XX-XXXX: easy to compare at a glance.</summary>
    private static string CodeOf(Guid id)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = SHA256.HashData(id.ToByteArray());
        var chars = bytes.Take(6).Select(b => alphabet[b % alphabet.Length]).ToArray();
        return $"{new string(chars, 0, 2)}-{new string(chars, 2, 4)}";
    }
}
