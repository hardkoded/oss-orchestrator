namespace OssOrchestrator.Agent;

public sealed record RepoSettings(string Repo, string Folder, bool Paused, bool Auto);

/// <summary>An open issue. Assigned: a person owns it. InProgress: a session is on it. Blocked: shown, never taken.</summary>
public sealed record IssueInfo(int Number, string Title, bool Assigned, bool InProgress = false, bool Blocked = false);

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
    /// in the last 15 minutes. Repos take turns (round robin), and each repo offers its lowest issue number first.
    /// </summary>
    public static IReadOnlyList<Launch> Plan(PlanInput input)
    {
        var slots = input.MaxTotal - input.Running;
        if (slots <= 0) return [];

        // Candidates per repo, lowest issue number first within a repo.
        var perRepo = new Dictionary<string, List<Launch>>();
        foreach (var repo in input.Repos)
        {
            if (!repo.Auto || repo.Paused) continue;
            if (!input.IssuesByRepo.TryGetValue(repo.Repo, out var issues)) continue;
            foreach (var issue in issues.OrderBy(i => i.Number))
            {
                if (issue.Assigned || issue.InProgress || issue.Blocked) continue;
                var key = $"{repo.Repo}#{issue.Number}";
                if (input.Ignored.Contains(key)) continue;
                if (input.LaunchedAt.TryGetValue(key, out var at) && input.NowMs - at < input.RecentLaunchMs) continue;
                if (!perRepo.TryGetValue(repo.Repo, out var list)) perRepo[repo.Repo] = list = [];
                list.Add(new Launch(repo.Repo, issue.Number));
            }
        }

        // Round robin across repos. The repo that launched longest ago goes first, so with fewer free slots than
        // repos, each repo gets a turn over successive passes. Ties keep the order of the repo list.
        var lastLaunch = new Dictionary<string, long>();
        foreach (var (key, at) in input.LaunchedAt)
        {
            var hash = key.LastIndexOf('#');
            if (hash < 0) continue;
            var repoName = key[..hash];
            lastLaunch[repoName] = Math.Max(lastLaunch.GetValueOrDefault(repoName), at);
        }
        var order = input.Repos
            .Select((r, index) => (r.Repo, index))
            .Where(x => perRepo.ContainsKey(x.Repo))
            .OrderBy(x => lastLaunch.GetValueOrDefault(x.Repo, 0))
            .ThenBy(x => x.index)
            .Select(x => x.Repo)
            .ToList();

        var queues = order.Select(name => new Queue<Launch>(perRepo[name])).ToList();
        var picked = new List<Launch>();
        while (picked.Count < slots && queues.Any(q => q.Count > 0))
        {
            foreach (var queue in queues)
            {
                if (picked.Count >= slots) break;
                if (queue.Count > 0) picked.Add(queue.Dequeue());
            }
        }
        return picked;
    }
}
