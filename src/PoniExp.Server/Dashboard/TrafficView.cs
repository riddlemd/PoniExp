using PoniExp.Server.Admin;
using PoniExp.Server.Machines;

namespace PoniExp.Server.Dashboard;

/// <summary>Where a traffic row ended up, for the dashboard's filter and colour.</summary>
public enum TrafficOutcome { Stored, HandedOver, Waiting, Expired, Dropped, Refused, JoinRequest }

/// <summary>
/// One message's whole trip through the router (or one event that isn't a message: a refused connection or a join request).
/// Only what the router sees: types and contents are sealed inside the encryption, so a receipt and a message look alike.
/// </summary>
public sealed record TrafficRow(
    DateTimeOffset StartedOn,
    DateTimeOffset LastOn,
    TrafficOutcome Outcome,
    string? From,
    string? To,
    Guid? MessageId,
    int? Size,
    long? Sequence,
    DateTimeOffset? RelayedOn,
    DateTimeOffset? DeliveredOn,
    int Deliveries,
    DateTimeOffset? ConfirmedOn,
    string? Address,
    string? Detail)
{
    /// <summary>What happened, in a sentence.</summary>
    public string Summary => Outcome switch
    {
        TrafficOutcome.Stored => $"{To} received and stored it",
        TrafficOutcome.HandedOver => $"handed to {To}, not stored yet",
        TrafficOutcome.Waiting => $"waiting for {To} to connect",
        TrafficOutcome.Expired => $"expired before {To} took it",
        TrafficOutcome.Dropped => $"dropped: {Detail}",
        TrafficOutcome.Refused => $"refused: {Detail}",
        TrafficOutcome.JoinRequest => $"\"{Detail}\" asked to join",
        _ => Outcome.ToString(),
    };

    public bool IsProblem => Outcome is TrafficOutcome.Expired or TrafficOutcome.Dropped or TrafficOutcome.Refused;

    /// <summary>From the router taking it to handing it over (first delivery), and from that to the recipient storing it.</summary>
    public TimeSpan? ToDeliver => RelayedOn is { } r && DeliveredOn is { } d ? d - r : null;

    public TimeSpan? ToStore => DeliveredOn is { } d && ConfirmedOn is { } c ? c - d : null;
}

public static class TrafficView
{
    /// <summary>Folds log entries (oldest first) into one row per message, newest first, at most <paramref name="limit"/>.</summary>
    public static IReadOnlyList<TrafficRow> Rows(IEnumerable<AdminTrafficEntry> entries, int limit)
    {
        var rows = new List<Builder>();
        var byMessage = new Dictionary<Guid, Builder>();
        foreach (var e in entries)
        {
            if (e.MessageId is not { } id)
            {
                rows.Add(new Builder(e));
                continue;
            }

            if (!byMessage.TryGetValue(id, out var row))
            {
                row = new Builder(e);
                byMessage[id] = row;
                rows.Add(row);
            }

            row.Add(e);
        }

        return [.. rows.Select(r => r.Build()).OrderByDescending(r => r.LastOn).Take(limit)];
    }

    /// <summary>
    /// Plain-language name of a duration. Seconds to one decimal under a minute, so a slow hand-over stands out from the
    /// usual milliseconds.
    /// </summary>
    public static string Duration(TimeSpan span) => span.TotalSeconds switch
    {
        < 1 => $"{Math.Max(0, (int)span.TotalMilliseconds)} ms",
        < 60 => $"{span.TotalSeconds:0.#} s",
        < 3600 => $"{(int)span.TotalMinutes} min {span.Seconds} s",
        _ => $"{(int)span.TotalHours} h {span.Minutes} min",
    };

    private sealed class Builder(AdminTrafficEntry first)
    {
        private readonly AdminTrafficEntry _first = first;
        private DateTimeOffset _last = first.OccurredOn;
        private DateTimeOffset? _relayed, _delivered, _confirmed;
        private int _deliveries;
        private TrafficOutcome? _end;
        private string? _from = first.From, _to = first.To, _address = first.Address, _detail = first.Detail;
        private int? _size = first.Size;
        private long? _sequence = first.Sequence;

        public void Add(AdminTrafficEntry e)
        {
            _last = e.OccurredOn > _last ? e.OccurredOn : _last;
            _from ??= e.From;
            _to ??= e.To;
            _size ??= e.Size;
            _sequence ??= e.Sequence;
            _address ??= e.Address;
            switch (e.Event)
            {
                case TrafficEvents.Relayed:
                    _relayed ??= e.OccurredOn;
                    break;
                case TrafficEvents.Delivered:
                    _delivered ??= e.OccurredOn;
                    _deliveries++;
                    break;
                case TrafficEvents.Confirmed:
                    _confirmed ??= e.OccurredOn;
                    break;
                case TrafficEvents.Expired:
                    _end = TrafficOutcome.Expired;
                    break;
                case TrafficEvents.Dropped:
                    (_end, _detail) = (TrafficOutcome.Dropped, e.Detail);
                    break;
                case TrafficEvents.Refused:
                    (_end, _detail) = (TrafficOutcome.Refused, e.Detail);
                    break;
            }
        }

        public TrafficRow Build()
        {
            var outcome = _first.MessageId is null
                ? _first.Event == TrafficEvents.JoinRequested ? TrafficOutcome.JoinRequest : TrafficOutcome.Refused
                : _end ?? (_confirmed is not null ? TrafficOutcome.Stored : _delivered is not null ? TrafficOutcome.HandedOver : TrafficOutcome.Waiting);
            return new TrafficRow(_first.OccurredOn, _last, outcome, _from, _to, _first.MessageId, _size, _sequence, _relayed, _delivered, _deliveries,
                _confirmed, _address, _detail);
        }
    }
}
