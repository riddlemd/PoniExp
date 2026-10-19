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
    private readonly RouterStore _store;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly TimeProvider _time;
    private readonly TrafficLog _traffic;
    private ImmutableDictionary<string, QueuedEnvelope> _queue;

    public MessageQueue(RouterStore store, IOptionsMonitor<RouterOptions> options, TimeProvider time, TrafficLog traffic)
    {
        _traffic = traffic;
        _store = store;
        _options = options;
        _time = time;
        _queue = store.LoadQueue()
            .Where(q => q.Envelope?.Ciphertext is not null) // entries from an older format can't be delivered
            .ToImmutableDictionary(q => q.Key);
    }

    /// <summary>Drops what the router no longer delivers (past its expiry).</summary>
    public Task PruneAsync() => UpdateAsync(Prune);

    public int Count => _queue.Count;

    /// <summary>Envelope bytes queued for <paramref name="machineId"/> (see <see cref="RouterOptions.MaxQueuedBytesPerRecipient"/>).</summary>
    public long QueuedBytesFor(string machineId) => _queue.Values.Where(q => q.RecipientMachine == machineId).Sum(q => (long)q.Envelope.Size);

    /// <summary>What the router would still deliver to <paramref name="machineId"/>, oldest first.</summary>
    public IReadOnlyList<Delivery> For(string machineId)
    {
        var now = _time.GetUtcNow();
        return _queue.Values
            .Where(q => q.RecipientMachine == machineId && q.ExpiresOn > now)
            .OrderBy(q => q.QueuedOn)
            .Select(q => new Delivery(q.Envelope, q.QueuedOn))
            .ToList();
    }

    /// <summary>When the router stops delivering this envelope, or null if it isn't queued.</summary>
    public DateTimeOffset? ExpiryOf(Guid id) => _queue.Values.FirstOrDefault(q => q.Envelope.Id == id)?.ExpiresOn;

    /// <summary>
    /// Adds the envelope, clamping its expiry. Returns the delivery to push, or null if one with the same id
    /// is already queued (a resend).
    /// </summary>
    /// <param name="recipients">For a room envelope, every machine it's wrapped for but the sender: one entry each.</param>
    public Task<Delivery?> EnqueueAsync(Envelope envelope, IReadOnlyCollection<string>? recipients = null)
    {
        var now = _time.GetUtcNow();
        var latest = now + _options.CurrentValue.MaxExpiry;
        return AddAsync(envelope, recipients, envelope.ExpiresOn < latest ? envelope.ExpiresOn : latest, now);
    }

    /// <summary>For tests that play a misbehaving router: queue an envelope as is, deliverable until <paramref name="deliverUntil"/>.</summary>
    internal Task<Delivery?> EnqueueUncheckedAsync(Envelope envelope, DateTimeOffset deliverUntil) =>
        AddAsync(envelope, null, deliverUntil, _time.GetUtcNow());

    /// <summary>Only the recipient can confirm (and so remove) an envelope.</summary>
    public async Task ConfirmAsync(string machineId, Guid id)
    {
        Envelope? confirmed = null;
        await UpdateAsync(queue =>
        {
            if ((queue.TryGetValue(id.ToString("D"), out var q) || queue.TryGetValue(QueuedEnvelope.RoomKey(id, machineId), out q))
                && q.RecipientMachine == machineId)
            {
                confirmed = q.Envelope;
                return queue.Remove(q.Key);
            }

            return queue;
        });
        if (confirmed is not null)
        {
            _traffic.Confirmed(confirmed);
        }
    }

    public async Task DropForAsync(string machineId)
    {
        List<Envelope> dropped = [];
        await UpdateAsync(queue =>
        {
            var gone = queue.Values.Where(q => q.RecipientMachine == machineId || q.Envelope.FromMachine == machineId).ToList();
            dropped = [.. gone.Select(q => q.Envelope)];
            return queue.RemoveRange(gone.Select(q => q.Key));
        });
        foreach (var e in dropped)
        {
            _traffic.Dropped(e, "its sender or recipient was removed");
        }
    }

    /// <summary>Removes what has expired (inside an update), logging each.</summary>
    private ImmutableDictionary<string, QueuedEnvelope> Prune(ImmutableDictionary<string, QueuedEnvelope> queue)
    {
        var now = _time.GetUtcNow();
        var expired = queue.Values.Where(q => q.ExpiresOn <= now).ToList();
        if (expired.Count == 0)
        {
            return queue;
        }

        foreach (var q in expired)
        {
            _traffic.Expired(q.Envelope);
        }

        return queue.RemoveRange(expired.Select(q => q.Key));
    }

    private async Task<Delivery?> AddAsync(Envelope envelope, IReadOnlyCollection<string>? recipients, DateTimeOffset expiresOn, DateTimeOffset now)
    {
        Delivery? added = null;
        await UpdateAsync(queue =>
        {
            var pruned = Prune(queue);
            if (pruned.Values.Any(q => q.Envelope.Id == envelope.Id))
            {
                return pruned;
            }

            added = new Delivery(envelope, now);
            _traffic.Relayed(envelope);
            var entries = recipients is null ? [new QueuedEnvelope(envelope, now, expiresOn)]
                : recipients.Distinct().Select(r => new QueuedEnvelope(envelope, now, expiresOn, r)).ToList();
            return pruned.AddRange(entries.Select(e => KeyValuePair.Create(e.Key, e)));
        });
        return added;
    }

    private async Task UpdateAsync(Func<ImmutableDictionary<string, QueuedEnvelope>, ImmutableDictionary<string, QueuedEnvelope>> change)
    {
        await _lock.WaitAsync();
        try
        {
            var next = change(_queue);
            if (!ReferenceEquals(next, _queue))
            {
                _store.SaveQueue(_queue, next);
                _queue = next;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// One queued delivery. <see cref="Recipient"/> is set for a room envelope's entries (one per member machine); for a
    /// message it's null and the recipient is its <c>toMachine</c>.
    /// </summary>
    public sealed record QueuedEnvelope(Envelope Envelope, DateTimeOffset QueuedOn, DateTimeOffset ExpiresOn, string? Recipient = null)
    {
        public string RecipientMachine => Recipient ?? Envelope.ToMachine;

        /// <summary>The message id for a message (as before rooms); id and machine for a room envelope's entry.</summary>
        public string Key => Recipient is null ? Envelope.Id.ToString("D") : RoomKey(Envelope.Id, Recipient);

        public static string RoomKey(Guid id, string recipient) => $"{id:D}/{recipient}";
    }
}
