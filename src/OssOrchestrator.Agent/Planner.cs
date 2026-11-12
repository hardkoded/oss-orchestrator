namespace OssOrchestrator.Agent;

public sealed record RepoSettings(string Repo, string Folder, bool Paused, bool Auto);

public sealed record IssueInfo(int Number, string Title, bool Assigned);

/// <summary>What the planner sees right now. Pure data, no I/O.</summary>
public sealed record PlanInput(
    int MaxTotal,
    int Running,
    IReadOnlyList<RepoSettings> Repos,
    IReadOnlyDictionary<string, IReadOnlyList<IssueInfo>> IssuesByRepo,
    IReadOnlySet<string> Ignored,              // "repo#n"
    IReadOnlyDictionary<string, long> LaunchedAt, // "repo#n" -> unix ms
    long NowMs,
    long RecentLaunchMs = 15 * 60 * 1000);

public sealed record Launch(string Repo, int Issue);

public static class Planner
{
    /// <summary>
    /// Picks which free issues to launch now. Global limit only (no per-repo limit).
    /// Only repos with Auto on and not Paused take part. Candidates are unassigned, not ignored and not launched
    /// in the last 15 minutes. Lowest issue number first, across all repos.
    /// </summary>
    public static IReadOnlyList<Launch> Plan(PlanInput input)
    {
        var slots = input.MaxTotal - input.Running;
        if (slots <= 0) return [];

        var candidates = new List<Launch>();
        foreach (var repo in input.Repos)
        {
            if (!repo.Auto || repo.Paused) continue;
            if (!input.IssuesByRepo.TryGetValue(repo.Repo, out var issues)) continue;
            foreach (var issue in issues)
            {
                if (issue.Assigned) continue;
                var key = $"{repo.Repo}#{issue.Number}";
                if (input.Ignored.Contains(key)) continue;
                if (input.LaunchedAt.TryGetValue(key, out var at) && input.NowMs - at < input.RecentLaunchMs) continue;
                candidates.Add(new Launch(repo.Repo, issue.Number));
            }
        }
        return candidates.OrderBy(c => c.Issue).Take(slots).ToList();
    }
}
