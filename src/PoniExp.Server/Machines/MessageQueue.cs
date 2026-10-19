using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;

namespace PoniExp.Server.Machines;

/// <summary>
/// Envelopes not yet confirmed by their recipient. An envelope stays here after it is pushed to a live
/// connection until the recipient confirms it (it has stored it), so a crash between push and storing
/// redelivers it on the next connect. Recipients drop duplicates by envelope id.
/// <para>
/// Each entry carries the time the router stops delivering it: the envelope's <c>expiresOn</c>, clamped to
/// <see cref="RouterOptions.MaxExpiry"/> after the router received it.
/// </para>
/// </summary>
public sealed class MessageQueue
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly TimeProvider _time;
    private ImmutableDictionary<Guid, QueuedEnvelope> _queue;

    public MessageQueue(IOptionsMonitor<RouterOptions> options, TimeProvider time)
    {
        var dataDirectory = options.CurrentValue.DataDirectory; // startup only
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "queue.json");
        _options = options;
        _time = time;
        _queue = (JsonFile.Read<List<QueuedEnvelope>>(_path) ?? [])
            .Where(q => q.Envelope?.Ciphertext is not null) // entries from an older format can't be delivered
            .ToImmutableDictionary(q => q.Envelope.Id);
    }

    public int Count => _queue.Count;

    /// <summary>What the router would still deliver to <paramref name="machineId"/>, oldest first.</summary>
    public IReadOnlyList<Delivery> For(string machineId)
    {
        var now = _time.GetUtcNow();
        return _queue.Values
            .Where(q => q.Envelope.ToMachine == machineId && q.ExpiresOn > now)
            .OrderBy(q => q.QueuedOn)
            .Select(q => new Delivery(q.Envelope, q.QueuedOn))
            .ToList();
    }

    /// <summary>When the router stops delivering this envelope, or null if it isn't queued.</summary>
    public DateTimeOffset? ExpiryOf(Guid id) => _queue.TryGetValue(id, out var q) ? q.ExpiresOn : null;

    /// <summary>
    /// Adds the envelope, clamping its expiry. Returns the delivery to push, or null if one with the same id
    /// is already queued (a resend).
    /// </summary>
    public Task<Delivery?> EnqueueAsync(Envelope envelope)
    {
        var now = _time.GetUtcNow();
        var latest = now + _options.CurrentValue.MaxExpiry;
        return AddAsync(envelope, envelope.ExpiresOn < latest ? envelope.ExpiresOn : latest, now);
    }

    /// <summary>For tests that play a misbehaving router: queue an envelope as is, deliverable until <paramref name="deliverUntil"/>.</summary>
    internal Task<Delivery?> EnqueueUncheckedAsync(Envelope envelope, DateTimeOffset deliverUntil) =>
        AddAsync(envelope, deliverUntil, _time.GetUtcNow());

    /// <summary>Only the recipient can confirm (and so remove) an envelope.</summary>
    public Task ConfirmAsync(string machineId, Guid id) =>
        UpdateAsync(queue => queue.TryGetValue(id, out var q) && q.Envelope.ToMachine == machineId ? queue.Remove(id) : queue);

    public Task DropForAsync(string machineId) =>
        UpdateAsync(queue => queue.RemoveRange(queue.Values
            .Where(q => q.Envelope.ToMachine == machineId || q.Envelope.FromMachine == machineId).Select(q => q.Envelope.Id)));

    private async Task<Delivery?> AddAsync(Envelope envelope, DateTimeOffset expiresOn, DateTimeOffset now)
    {
        Delivery? added = null;
        await UpdateAsync(queue =>
        {
            var pruned = queue.RemoveRange(queue.Values.Where(q => q.ExpiresOn <= now).Select(q => q.Envelope.Id));
            if (pruned.ContainsKey(envelope.Id))
            {
                return pruned;
            }

            added = new Delivery(envelope, now);
            return pruned.Add(envelope.Id, new QueuedEnvelope(envelope, now, expiresOn));
        });
        return added;
    }

    private async Task UpdateAsync(Func<ImmutableDictionary<Guid, QueuedEnvelope>, ImmutableDictionary<Guid, QueuedEnvelope>> change)
    {
        await _lock.WaitAsync();
        try
        {
            var next = change(_queue);
            if (!ReferenceEquals(next, _queue))
            {
                JsonFile.Write(_path, next.Values.OrderBy(q => q.QueuedOn).ToList());
                _queue = next;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public sealed record QueuedEnvelope(Envelope Envelope, DateTimeOffset QueuedOn, DateTimeOffset ExpiresOn);
}
