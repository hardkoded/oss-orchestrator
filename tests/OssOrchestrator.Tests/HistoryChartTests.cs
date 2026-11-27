using OssOrchestrator.Web;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Tests;

public class HistoryChartTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    private static HistoryPoint Point(double daysAgo, int open = 10, int? sessions = null) =>
        new() { At = Now.AddDays(-daysAgo), Open = open, Sessions = sessions, Running = sessions };

    private static HistoryDoc Doc(string id, params HistoryPoint[] points) => new() { Id = id, Points = [.. points] };

    [Fact]
    public void A_backfilled_point_counts_only_before_the_first_recorded_one()
    {
        var points = HistoryChart.Points(
        [
            Doc("history-2026-11-25", Point(2, open: 20)),
            Doc("history-backfill", Point(3, open: 1), Point(1, open: 2)),
        ], Now);

        Assert.Equal([1, 20], points.Select(p => p.Open));
    }

    [Fact]
    public void Only_the_last_30_days_are_drawn() =>
        Assert.Equal([2], HistoryChart.Points([Doc("history-backfill", Point(31, open: 1), Point(29, open: 2))], Now).Select(p => p.Open));

    [Fact]
    public void The_last_point_of_each_4_hours_is_kept()
    {
        var points = HistoryChart.Points([Doc("history-2026-11-26", Point(0.99, open: 1), Point(0.98, open: 2), Point(0.5, open: 3))], Now);

        Assert.Equal([2, 3], points.Select(p => p.Open));
    }

    [Fact]
    public void The_dates_go_back_from_today_every_5_days()
    {
        var dates = HistoryChart.Dates(Now.AddHours(12));

        Assert.Equal(["Nov 2", "Nov 7", "Nov 12", "Nov 17", "Nov 22", "Nov 27"], dates.Select(d => d.Label));
        Assert.Equal(15, dates[0].Percent, 1);
        Assert.Equal(98.3, dates[^1].Percent, 1);
    }

    [Fact]
    public void A_line_runs_from_the_oldest_to_the_newest_value()
    {
        var open = HistoryChart.Build([Point(30, open: 5), Point(29.5, open: 15), Point(29, open: 10)], Now)[0];

        Assert.Equal([0, 5, 10, 15], open.Ticks);
        Assert.Equal("M0 100H600M0 66.7H600M0 33.3H600M0 0H600", open.Grid);
        Assert.Equal("M0 66.7L10 0L20 33.3", open.Lines[0].Path);
        Assert.Equal("M20 33.3h0", open.Lines[0].Dots);
        Assert.Equal(10, open.Lines[0].Latest);
    }

    [Fact]
    public void A_gap_of_more_than_25_hours_breaks_the_line()
    {
        var line = HistoryChart.Build([Point(30, open: 0), Point(24, open: 10), Point(23, open: 10)], Now)[0].Lines[0];

        Assert.Equal("M120 0L140 0", line.Path);
        // The value that stands alone and the newest one.
        Assert.Equal("M0 100h0M140 0h0", line.Dots);
    }

    [Fact]
    public void Session_lines_skip_backfilled_points()
    {
        var sessions = HistoryChart.Build([Point(30), Point(0, sessions: 4)], Now)[2];

        Assert.Equal([0, 1, 2, 3, 4], sessions.Ticks);
        Assert.All(sessions.Lines, l => Assert.Equal(("", "M600 0h0"), (l.Path, l.Dots)));
    }

    [Fact]
    public void Hover_shows_each_line_at_each_point_that_has_a_value()
    {
        var charts = HistoryChart.Build([Point(30, open: 5), Point(0, open: 9, sessions: 4)], Now);
        var (then, now) = (Now.AddDays(-30).ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds());

        Assert.Equal($"[[0,{then},5],[600,{now},9]]", charts[0].Readings);
        Assert.Equal($"[[600,{now},4,4]]", charts[2].Readings);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(7, 1, 7)]
    [InlineData(8, 2, 8)]
    [InlineData(15, 5, 15)]
    [InlineData(108, 20, 120)]
    [InlineData(330, 50, 350)]
    [InlineData(1200, 200, 1200)]
    public void The_axis_starts_at_zero_and_has_round_steps(int open, int step, int top)
    {
        var ticks = HistoryChart.Build([Point(0, open)], Now)[0].Ticks;

        Assert.Equal(Enumerable.Range(0, top / step + 1).Select(n => n * step), ticks);
    }

    [Fact]
    public void An_empty_chart_still_has_a_range()
    {
        var charts = HistoryChart.Build([Point(1), Point(0)], Now);

        Assert.Equal([0, 1], charts[2].Ticks);
        Assert.All(charts[2].Lines, l => Assert.Equal((null, "", ""), (l.Latest, l.Path, l.Dots)));
    }
}
