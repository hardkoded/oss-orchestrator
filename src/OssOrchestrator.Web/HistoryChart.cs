using System.Globalization;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web;

/// <summary>One line of a chart, as SVG paths. Dots marks the newest value, and each value that stands alone.</summary>
public sealed record ChartLine(string Color, string Label, int? Latest, string Path, string Dots);

public sealed record Chart(string Title, int Min, int Max, IReadOnlyList<ChartLine> Lines);

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

    public static IReadOnlyList<Chart> Build(IReadOnlyList<HistoryPoint> points, DateTimeOffset now) =>
    [
        // Open issues show their own range, so a slow trend is visible. The other counts start at zero.
        Make("Open issues", false, points, now, ("work", "open", p => p.Open)),
        Make("PRs merged in 24h", true, points, now, ("ready", "merged", p => p.Merged24h)),
        Make("Sessions", true, points, now, ("work", "open sessions", p => p.Sessions), ("slot", "using a work slot", p => p.Running)),
    ];

    private static Chart Make(string title, bool fromZero, IReadOnlyList<HistoryPoint> points, DateTimeOffset now,
        params (string Color, string Label, Func<HistoryPoint, int?> Value)[] lines)
    {
        var values = lines.SelectMany(l => points.Select(l.Value)).OfType<int>().ToList();
        var min = fromZero || values.Count == 0 ? 0 : values.Min();
        var max = values.Count == 0 ? 0 : values.Max();
        // A flat line needs a range around it.
        if (max == min) (min, max) = (fromZero ? 0 : Math.Max(0, min - 1), max + 1);
        var start = now.AddDays(-Days);

        return new Chart(title, min, max, lines.Select(line =>
        {
            var known = points.Where(p => line.Value(p) is not null).ToList();
            var segments = new List<List<string>>();
            for (var i = 0; i < known.Count; i++)
            {
                if (i == 0 || known[i].At - known[i - 1].At > Gap) segments.Add([]);
                var x = Math.Clamp((known[i].At - start) / TimeSpan.FromDays(Days), 0, 1) * Width;
                var y = Height - (line.Value(known[i])!.Value - min) * (double)Height / (max - min);
                segments[^1].Add(string.Create(CultureInfo.InvariantCulture, $"{x:0.#} {y:0.#}"));
            }
            // A zero-length line with round ends draws a dot that keeps its shape when the chart is stretched.
            var dots = segments.Where((s, i) => s.Count == 1 || i == segments.Count - 1).Select(s => $"M{s[^1]}h0");
            return new ChartLine(line.Color, line.Label,
                known.Count > 0 ? line.Value(known[^1]) : null,
                string.Concat(segments.Where(s => s.Count > 1).Select(s => $"M{string.Join('L', s)}")),
                string.Concat(dots));
        }).ToList());
    }
}
