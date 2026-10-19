using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using PoniExp.Core.Protocol;

namespace PoniExp.Router.Machines;

/// <summary>
/// Envelopes not yet acknowledged by their recipient. An envelope stays here after it is pushed to a live
/// connection until the recipient acks it, so a crash between push and processing redelivers it on the
/// next connect. Recipients drop duplicates by envelope id.
/// </summary>
public sealed class MessageQueue
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;
    private ImmutableDictionary<Guid, QueuedEnvelope> _queue;

    public MessageQueue(IOptions<RouterOptions> options, TimeProvider time)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _path = Path.Combine(options.Value.DataDirectory, "queue.json");
        _retention = options.Value.QueueRetention;
        _time = time;
        _queue = (JsonFile.Read<List<QueuedEnvelope>>(_path) ?? []).ToImmutableDictionary(q => q.Envelope.Id);
    }

    public int Count => _queue.Count;

    public IReadOnlyList<Envelope> For(string machineId) =>
        _queue.Values.Where(q => q.Envelope.To == machineId).OrderBy(q => q.QueuedAt).Select(q => q.Envelope).ToList();

    /// <summary>Adds the envelope. False if one with the same id is already queued (a resend).</summary>
    public async Task<bool> EnqueueAsync(Envelope envelope)
    {
        var added = false;
        await UpdateAsync(queue =>
        {
            var cutoff = _time.GetUtcNow() - _retention;
            var pruned = queue.RemoveRange(queue.Values.Where(q => q.QueuedAt < cutoff).Select(q => q.Envelope.Id));
            if (pruned.ContainsKey(envelope.Id))
            {
                return pruned;
            }

            added = true;
            return pruned.Add(envelope.Id, new QueuedEnvelope(envelope, _time.GetUtcNow()));
        });
        return added;
    }

    /// <summary>Only the recipient can acknowledge (and so remove) an envelope.</summary>
    public Task AckAsync(string machineId, Guid id) =>
        UpdateAsync(queue => queue.TryGetValue(id, out var q) && q.Envelope.To == machineId ? queue.Remove(id) : queue);

    public Task DropForAsync(string machineId) =>
        UpdateAsync(queue => queue.RemoveRange(queue.Values
            .Where(q => q.Envelope.To == machineId || q.Envelope.From == machineId).Select(q => q.Envelope.Id)));

    private async Task UpdateAsync(Func<ImmutableDictionary<Guid, QueuedEnvelope>, ImmutableDictionary<Guid, QueuedEnvelope>> change)
    {
        await _lock.WaitAsync();
        try
        {
            var next = change(_queue);
            if (!ReferenceEquals(next, _queue))
            {
                JsonFile.Write(_path, next.Values.OrderBy(q => q.QueuedAt).ToList());
                _queue = next;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public sealed record QueuedEnvelope(Envelope Envelope, DateTimeOffset QueuedAt);
}
