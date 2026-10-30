using PoniExp.Server.Admin;
using PoniExp.Server.Dashboard;
using Xunit;

namespace PoniExp.Tests;

public sealed class TrafficViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 17, 13, 4, TimeSpan.Zero);

    private static AdminTrafficEntry Entry(string @event, Guid? id, double seconds, string? detail = null, string? address = null) =>
        new(T0.AddSeconds(seconds), @event, id is null ? "blinky" : "ise", id is null ? null : "blinky", id, id is null ? null : 484, id is null ? null : 7,
            address, detail);

    [Fact]
    public void Given_a_relayed_delivered_and_confirmed_message_When_folded_Then_it_is_one_stored_row_with_its_step_times()
    {
        // Given
        var id = Guid.NewGuid();
        var log = new[] { Entry("relayed", id, 0), Entry("delivered", id, 0.02), Entry("confirmed", id, 0.5) };

        // When
        var rows = TrafficView.Rows(log, 10);

        // Then
        var row = Assert.Single(rows);
        Assert.Equal(TrafficOutcome.Stored, row.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(0.02), row.ToDeliver);
        Assert.Equal(TimeSpan.FromSeconds(0.48), row.ToStore);
        Assert.Equal("blinky received and stored it", row.Summary);
        Assert.Equal((484, 7L), (row.Size!.Value, row.Sequence!.Value));
    }

    [Fact]
    public void Given_a_message_only_relayed_When_folded_Then_it_waits_for_its_recipient()
    {
        // Given
        var id = Guid.NewGuid();

        // When
        var row = Assert.Single(TrafficView.Rows([Entry("relayed", id, 0)], 10));

        // Then
        Assert.Equal(TrafficOutcome.Waiting, row.Outcome);
        Assert.False(row.IsProblem);
        Assert.Null(row.ToDeliver);
    }

    [Fact]
    public void Given_a_message_handed_over_twice_but_not_stored_When_folded_Then_it_counts_both_tries_and_times_the_first()
    {
        // Given
        var id = Guid.NewGuid();
        var log = new[] { Entry("relayed", id, 0), Entry("delivered", id, 1), Entry("delivered", id, 30) };

        // When
        var row = Assert.Single(TrafficView.Rows(log, 10));

        // Then
        Assert.Equal(TrafficOutcome.HandedOver, row.Outcome);
        Assert.Equal(2, row.Deliveries);
        Assert.Equal(TimeSpan.FromSeconds(1), row.ToDeliver);
        Assert.Equal(T0.AddSeconds(30), row.LastOn);
    }

    [Theory]
    [InlineData("expired", TrafficOutcome.Expired, "expired before blinky took it")]
    [InlineData("dropped", TrafficOutcome.Dropped, "dropped: why")]
    [InlineData("refused", TrafficOutcome.Refused, "refused: why")]
    public void Given_a_message_that_ended_badly_When_folded_Then_it_is_a_problem_with_its_reason(string ending, TrafficOutcome outcome, string summary)
    {
        // Given
        var id = Guid.NewGuid();
        var log = new[] { Entry("relayed", id, 0), Entry("delivered", id, 1), Entry(ending, id, 2, detail: "why") };

        // When
        var row = Assert.Single(TrafficView.Rows(log, 10));

        // Then
        Assert.Equal(outcome, row.Outcome);
        Assert.True(row.IsProblem);
        Assert.Equal(summary, row.Summary);
    }

    [Fact]
    public void Given_a_refused_connection_and_a_join_request_When_folded_Then_each_is_its_own_row_with_its_address()
    {
        // Given
        var log = new[] { Entry("refused", null, 0, "connection: bad key", "100.1.2.3"), Entry("join-requested", null, 1, "dash-test", "100.1.2.4") };

        // When
        var rows = TrafficView.Rows(log, 10);

        // Then
        Assert.Collection(rows,
            join => Assert.Equal((TrafficOutcome.JoinRequest, "100.1.2.4", "\"dash-test\" asked to join"), (join.Outcome, join.Address, join.Summary)),
            refused => Assert.Equal((TrafficOutcome.Refused, "100.1.2.3"), (refused.Outcome, refused.Address)));
    }

    [Fact]
    public void Given_more_rows_than_the_limit_When_folded_Then_the_newest_are_kept_newest_first()
    {
        // Given
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var log = ids.Select((id, i) => Entry("relayed", id, i));

        // When
        var rows = TrafficView.Rows(log, 2);

        // Then
        Assert.Equal([ids[2], ids[1]], rows.Select(r => r.MessageId!.Value));
    }

    [Theory]
    [InlineData(0.02, "20 ms")]
    [InlineData(1.5, "1.5 s")]
    [InlineData(125, "2 min 5 s")]
    [InlineData(7260, "2 h 1 min")]
    public void Given_a_duration_When_named_Then_it_uses_the_largest_sensible_unit(double seconds, string expected)
    {
        // Given
        var span = TimeSpan.FromSeconds(seconds);

        // When
        var name = TrafficView.Duration(span);

        // Then
        Assert.Equal(expected, name);
    }
}
