using System.Globalization;
using System.Text.Json;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web;

/// <summary>One line of a chart, as SVG paths. Dots marks the newest value, and each value that stands alone.</summary>
public sealed record ChartLine(string Color, string Label, int? Latest, string Path, string Dots);

/// <summary>
/// A chart from zero to its last tick. Grid is the SVG path of one horizontal line for each tick. Readings is what the
/// page shows on hover: JSON rows of the place on the x axis, the time in Unix milliseconds, and one value for each line.
/// </summary>
public sealed record Chart(string Title, IReadOnlyList<int> Ticks, string Grid, IReadOnlyList<ChartLine> Lines, string Readings);

/// <summary>Turns the recorded history into the charts of the History tab, all over the same time axis.</summary>
public static class HistoryChart
{
    public const int Days = 30;
    /// <summary>The size of the SVG view box. The page stretches it to its width.</summary>
    public const int Width = 600, Height = 100;
    private static readonly long BucketSeconds = (long)TimeSpan.FromHours(4).TotalSeconds;
    // Longer than the day between two backfilled points.
    private static readonly TimeSpan Gap = TimeSpan.FromHours(25);

    /// <summary>
    /// The points to draw, oldest first: the last one of each 4 hours, so the page stays small. A backfilled point
    /// counts only before the first recorded one.
    /// </summary>
    public static List<HistoryPoint> Points(IReadOnlyList<HistoryDoc> docs, DateTimeOffset now)
    {
        var recorded = docs.Where(d => d.Id != "history-backfill").SelectMany(d => d.Points).ToList();
        var firstRecorded = recorded.Count > 0 ? recorded.Min(p => p.At) : DateTimeOffset.MaxValue;
        return docs.Where(d => d.Id == "history-backfill").SelectMany(d => d.Points)
            .Where(p => p.At < firstRecorded)
            .Concat(recorded)
            .Where(p => p.At >= now.AddDays(-Days))
            .OrderBy(p => p.At)
            .GroupBy(p => p.At.ToUnixTimeSeconds() / BucketSeconds)
            .Select(bucket => bucket.Last())
            .ToList();
    }

    /// <summary>The dates under each chart, every 5 days back from today, with their place on the time axis from 0 to 100.</summary>
    public static IReadOnlyList<(double Percent, string Label)> Dates(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return [.. Enumerable.Range(0, 6)
            .Select(i => today.AddDays(5 * (i - 5)))
            .Select(day => ((day - now.AddDays(-Days)) / TimeSpan.FromDays(Days) * 100, day.ToString("MMM d", CultureInfo.InvariantCulture)))];
    }

    public static IReadOnlyList<Chart> Build(IReadOnlyList<HistoryPoint> points, DateTimeOffset now) =>
    [
        Make("Open issues", points, now, ("work", "open", p => p.Open)),
        Make("PRs merged in 24h", points, now, ("ready", "merged", p => p.Merged24h)),
        Make("Sessions", points, now, ("work", "open sessions", p => p.Sessions), ("slot", "using a work slot", p => p.Running)),
    ];

    /// <summary>Zero and up to 7 equal steps that reach <paramref name="max"/>. A step is 1, 2 or 5 times a power of ten.</summary>
    private static int[] Ticks(int max)
    {
        var step = 1;
        for (var i = 0; max > step * 7; i++) step = i % 3 == 1 ? step * 5 / 2 : step * 2;
        return [.. Enumerable.Range(0, Math.Max(1, (max + step - 1) / step) + 1).Select(n => n * step)];
    }

    private static Chart Make(string title, IReadOnlyList<HistoryPoint> points, DateTimeOffset now,
        params (string Color, string Label, Func<HistoryPoint, int?> Value)[] lines)
    {
        var ticks = Ticks(lines.SelectMany(l => points.Select(l.Value)).Max() ?? 0);
        double Y(int value) => Height - value * (double)Height / ticks[^1];
        var grid = string.Concat(ticks.Select(t => string.Create(CultureInfo.InvariantCulture, $"M0 {Y(t):0.#}H{Width}")));
        var start = now.AddDays(-Days);
        double X(DateTimeOffset at) => Math.Clamp((at - start) / TimeSpan.FromDays(Days), 0, 1) * Width;
        var readings = JsonSerializer.Serialize(points
            .Where(p => lines.Any(l => l.Value(p) is not null))
            .Select(p => new object?[] { Math.Round(X(p.At), 1), p.At.ToUnixTimeMilliseconds() }
                .Concat(lines.Select(l => (object?)l.Value(p)))));

        return new Chart(title, ticks, grid, lines.Select(line =>
        {
            var known = points.Where(p => line.Value(p) is not null).ToList();
            var segments = new List<List<string>>();
            for (var i = 0; i < known.Count; i++)
            {
                if (i == 0 || known[i].At - known[i - 1].At > Gap) segments.Add([]);
                var x = X(known[i].At);
                var y = Y(line.Value(known[i])!.Value);
                segments[^1].Add(string.Create(CultureInfo.InvariantCulture, $"{x:0.#} {y:0.#}"));
            }
            // A zero-length line with round ends draws a dot that keeps its shape when the chart is stretched.
            var dots = segments.Where((s, i) => s.Count == 1 || i == segments.Count - 1).Select(s => $"M{s[^1]}h0");
            return new ChartLine(line.Color, line.Label,
                known.Count > 0 ? line.Value(known[^1]) : null,
                string.Concat(segments.Where(s => s.Count > 1).Select(s => $"M{string.Join('L', s)}")),
                string.Concat(dots));
        }).ToList(), readings);
    }
}
