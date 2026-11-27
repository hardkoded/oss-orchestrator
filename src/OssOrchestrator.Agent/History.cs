namespace OssOrchestrator.Agent;

/// <summary>An issue's life, to count the open ones at a past moment. ClosedAt is null while it is open.</summary>
public sealed record IssueSpan(DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt);

/// <summary>What goes into the history the dashboard charts.</summary>
public static class History
{
    public const string BackfillId = "history-backfill";
    public const int BackfillDays = 30;

    /// <summary>The id of the document that holds the points of the moment's UTC day.</summary>
    public static string DayId(DateTimeOffset at) => $"history-{at.UtcDateTime:yyyy-MM-dd}";

    /// <summary>
    /// True when a number changed, or the last point is an hour old. The hourly point tells "nothing changed" apart from
    /// "the agent was off".
    /// </summary>
    public static bool ShouldAdd(HistoryPoint? last, HistoryPoint next) =>
        last is null || next.At - last.At >= TimeSpan.FromHours(1)
        || (last.Open, last.Merged24h, last.Sessions, last.Running) != (next.Open, next.Merged24h, next.Sessions, next.Running);

    /// <summary>The oldest UTC midnight the backfill covers.</summary>
    public static DateTimeOffset FirstBackfillDay(DateTimeOffset now) => new(now.UtcDateTime.Date.AddDays(1 - BackfillDays), TimeSpan.Zero);

    /// <summary>One point for each of the last UTC midnights. A reopened issue counts as open while it was closed.</summary>
    public static List<HistoryPoint> Backfill(IReadOnlyList<IssueSpan> issues, IReadOnlyList<DateTimeOffset> merged, DateTimeOffset now) =>
        Enumerable.Range(0, BackfillDays)
            .Select(day => FirstBackfillDay(now).AddDays(day))
            .Select(at => new HistoryPoint
            {
                At = at,
                Open = issues.Count(i => i.CreatedAt <= at && (i.ClosedAt is null || i.ClosedAt > at)),
                Merged24h = merged.Count(m => m > at.AddDays(-1) && m <= at),
            })
            .ToList();
}
