using System.Text.Json;
using System.Text.RegularExpressions;

namespace OssOrchestrator.Agent;

/// <summary>Where a blocking issue or PR stands. Done is a merged PR or an issue closed as completed. Done and Closed both end the wait.</summary>
public enum BlockerState { Open, Done, Closed }

/// <summary>
/// A worker that cannot go on until another issue or PR of the same repo is done adds the waiting-on label and posts a
/// comment with the marker &lt;!-- orc:blocked-by #123 --&gt;. The comment says what it waits on, the label says it waits.
/// </summary>
public static partial class BlockedBy
{
    [GeneratedRegex(@"<!--\s*orc:blocked-by\s+#(\d+)\s*-->")]
    private static partial Regex Marker();

    /// <summary>
    /// The number in the newest marker comment by one of <paramref name="own"/>, or null. Comments by anyone else are
    /// untrusted and never count. A marker that points at the issue itself is ignored.
    /// </summary>
    public static int? FromComments(JsonElement issue, int self, ISet<string> own)
    {
        if (!issue.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) return null;
        int? found = null;
        foreach (var comment in comments.EnumerateArray())
        {
            var login = comment.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var l) ? l.GetString() : null;
            if (login is null || !own.Contains(login)) continue;
            var body = comment.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var matches = Marker().Matches(body);
            if (matches.Count == 0 || !int.TryParse(matches[^1].Groups[1].Value, out var number) || number == self) continue;
            found = number;   // comments come oldest first, so the last marker wins
        }
        return found;
    }

    /// <summary>What to tell the worker once its blocker is no longer open.</summary>
    public static string ResumeMessage(int blocker, BlockerState state) => state == BlockerState.Done
        ? $"#{blocker} is done (merged or completed). Rebase on the updated base branch and continue your work."
        : $"#{blocker} was closed without being done (not merged, or closed as not planned). Decide how to continue, and ask me if the change you waited for is still needed.";
}
