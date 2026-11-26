using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web;

/// <summary>Plain words for the commands the dashboard queued.</summary>
public static class Activity
{
    private static readonly string[] Problems = ["could not", "unknown", "missing", "limit must", "dry run"];

    public static string Describe(CommandDoc c)
    {
        var repo = c.Repo is null ? "" : c.Repo[(c.Repo.IndexOf('/') + 1)..];
        var issue = $"{repo} #{c.Issue}";
        // A value read back from the database is a JSON element, so it is compared as text.
        var value = c.Value?.ToString();
        return c.Type switch
        {
            "pause_all" => "Pause new work",
            "resume_all" => "Resume new work",
            "pause" => $"Pause {repo}",
            "resume" => $"Resume {repo}",
            "auto" => $"{(string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? "Take" : "Stop taking")} new issues from {repo}",
            "limit" => $"Set work slots to {value}",
            "ignore" => $"Ignore {issue}",
            "restore" => $"Restore {issue}",
            "block_session" => $"Park {issue}",
            "unblock" => $"Return {issue} to the queue",
            "refresh" => "Check now",
            _ => c.Type,
        };
    }

    /// <summary>The agent's result, only when it says the command did not do what was asked.</summary>
    public static string? Problem(CommandDoc c) =>
        c.Result is { } result && Problems.Any(p => result.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? result : null;
}
