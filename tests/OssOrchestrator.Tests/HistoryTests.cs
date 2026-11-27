using OssOrchestrator.Agent;

namespace OssOrchestrator.Tests;

public class HistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Midnight = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    private static HistoryPoint Point(int minutes = 0, int open = 10, int merged = 2, int? sessions = 4, int? running = 1) =>
        new() { At = Now.AddMinutes(minutes), Open = open, Merged24h = merged, Sessions = sessions, Running = running };

    [Fact]
    public void The_first_point_of_a_day_is_added() => Assert.True(History.ShouldAdd(null, Point()));

    [Fact]
    public void Unchanged_numbers_are_not_added_again() => Assert.False(History.ShouldAdd(Point(), Point(59)));

    [Fact]
    public void Unchanged_numbers_are_added_after_an_hour() => Assert.True(History.ShouldAdd(Point(), Point(60)));

    [Theory]
    [InlineData(11, 2, 4, 1)]
    [InlineData(10, 3, 4, 1)]
    [InlineData(10, 2, 5, 1)]
    [InlineData(10, 2, 4, 2)]
    public void A_change_in_any_number_is_added(int open, int merged, int sessions, int running) =>
        Assert.True(History.ShouldAdd(Point(), Point(1, open, merged, sessions, running)));

    [Fact]
    public void A_day_is_a_UTC_day() =>
        Assert.Equal("history-2026-11-28", History.DayId(new DateTimeOffset(2026, 11, 27, 22, 0, 0, TimeSpan.FromHours(-3))));

    [Fact]
    public void The_backfill_has_one_point_for_each_of_the_last_30_midnights()
    {
        var points = History.Backfill([], [], Now);

        Assert.Equal(30, points.Count);
        Assert.Equal(Midnight.AddDays(-29), points[0].At);
        Assert.Equal(History.FirstBackfillDay(Now), points[0].At);
        Assert.Equal(Midnight, points[^1].At);
        Assert.All(points, p => Assert.Equal((null, null), (p.Sessions, p.Running)));
    }

    [Fact]
    public void An_issue_is_open_from_its_creation_until_it_is_closed()
    {
        var points = History.Backfill(
        [
            new IssueSpan(Midnight.AddDays(-100), null),
            new IssueSpan(Midnight.AddDays(-2), Midnight.AddDays(-1)),
            new IssueSpan(Midnight.AddHours(1), null),
        ], [], Now);

        Assert.Equal([1, 2, 1, 1], points.TakeLast(4).Select(p => p.Open));
    }

    [Fact]
    public void A_merge_counts_for_the_24_hours_after_it()
    {
        var points = History.Backfill([], [Midnight.AddDays(-2), Midnight.AddDays(-1).AddHours(1), Midnight.AddHours(1)], Now);

        Assert.Equal([1, 0, 1], points.TakeLast(3).Select(p => p.Merged24h));
    }
}
