using System.Text.Json;
using System.Text.RegularExpressions;

namespace OssOrchestrator.Agent;

/// <summary>Where a blocking issue or PR stands. Done is a merged PR or an issue closed as completed. The wait ends when every blocker is Done or Closed.</summary>
public enum BlockerState { Open, Done, Closed }

/// <summary>
/// A worker that cannot go on until other issues or PRs of the same repo are done adds the waiting-on label and posts a
/// comment with one marker &lt;!-- orc:blocked-by #123 --&gt; for each. The comment says what it waits on, the label says it waits.
/// </summary>
public static partial class BlockedBy
{
    [GeneratedRegex(@"<!--\s*orc:blocked-by\s+#(\d+)\s*-->")]
    private static partial Regex Marker();

    /// <summary>
    /// The numbers in the newest comment with a marker by one of <paramref name="own"/>. That comment is the whole list:
    /// it replaces earlier ones. Comments by anyone else are untrusted and never count. A marker that points at the issue
    /// itself is ignored.
    /// </summary>
    public static IReadOnlyList<int> FromComments(JsonElement issue, int self, ISet<string> own)
    {
        if (!issue.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) return [];
        IReadOnlyList<int> found = [];
        foreach (var comment in comments.EnumerateArray())
        {
            var login = comment.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var l) ? l.GetString() : null;
            if (login is null || !own.Contains(login)) continue;
            var body = comment.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var numbers = Marker().Matches(body)
                .Select(m => int.TryParse(m.Groups[1].Value, out var number) ? number : self)
                .Where(number => number != self).Distinct().ToList();
            if (numbers.Count > 0) found = numbers;   // comments come oldest first, so the last one wins
        }
        return found;
    }

    /// <summary>What to tell the worker once none of its blockers is open.</summary>
    public static string ResumeMessage(IReadOnlyList<(int Number, BlockerState State)> blockers)
    {
        var done = string.Join(", ", blockers.Where(b => b.State == BlockerState.Done).Select(b => $"#{b.Number}"));
        var closed = string.Join(", ", blockers.Where(b => b.State == BlockerState.Closed).Select(b => $"#{b.Number}"));
        var lines = new List<string>();
        if (done.Length > 0) lines.Add($"Done (merged or completed): {done}. Rebase on the updated base branch and continue your work.");
        if (closed.Length > 0) lines.Add($"Closed without being done (not merged, or closed as not planned): {closed}. Decide how to continue, and ask me if a change you waited for is still needed.");
        return string.Join(" ", lines);
    }
}
