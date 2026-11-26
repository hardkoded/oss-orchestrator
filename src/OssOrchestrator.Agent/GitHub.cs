using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OssOrchestrator.Agent;

/// <summary>An open PR. Issue is parsed from the branch name, when it carries one.</summary>
public sealed record PrInfo(int Number, string Url, int? Issue, bool IsDraft, string Mergeable, string Status, string Body = "", bool ChecksRunning = false);

/// <summary>
/// The PR status shown on the dashboard: conflicted, failed, building, ready, or pending (no PR or no checks yet).
/// Conflicts win over checks, failures win over still-running checks.
/// </summary>
public static class PrStatus
{
    private static readonly HashSet<string> FailedConclusions =
        ["FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE", "ERROR"];

    public static string From(string mergeable, JsonElement rollup)
    {
        if (mergeable == "CONFLICTING") return "conflicted";
        if (rollup.ValueKind != JsonValueKind.Array || rollup.GetArrayLength() == 0) return "pending";

        var (failed, running) = Scan(rollup);
        return failed ? "failed" : running ? "building" : "ready";
    }

    /// <summary>True while any check has not finished. A red build is only reported to a worker once this is false.</summary>
    public static bool AnyRunning(JsonElement rollup) => rollup.ValueKind == JsonValueKind.Array && Scan(rollup).Running;

    private static (bool Failed, bool Running) Scan(JsonElement rollup)
    {
        var failed = false;
        var running = false;
        foreach (var check in rollup.EnumerateArray())
        {
            // CheckRun has status/conclusion; StatusContext has state.
            var status = Str(check, "status");
            var conclusion = Str(check, "conclusion");
            var state = Str(check, "state");
            if (status is not null && status != "COMPLETED") running = true;
            if (conclusion is not null && FailedConclusions.Contains(conclusion)) failed = true;
            if (state is "FAILURE" or "ERROR") failed = true;
            if (state is "PENDING" or "EXPECTED") running = true;
        }

        return (failed, running);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Reads issues and PRs through the gh CLI (the user's own login, nothing stored here).</summary>
public sealed class GitHub
{
    public async Task<IReadOnlyList<IssueInfo>> OpenIssuesAsync(RepoSettings repo, CancellationToken ct)
    {
        var json = await RunAsync(repo.Folder, ct, "issue", "list", "--state", "open", "--limit", "500",
            "--json", "number,title,assignees,labels");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e =>
            {
                var labels = e.GetProperty("labels").EnumerateArray()
                    .Select(l => l.GetProperty("name").GetString() ?? "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new IssueInfo(
                    e.GetProperty("number").GetInt32(),
                    e.GetProperty("title").GetString() ?? "",
                    e.GetProperty("assignees").GetArrayLength() > 0,
                    InProgress: labels.Contains(IssueLabels.InProgress),
                    Blocked: labels.Contains(IssueLabels.Blocked));
            })
            .ToList();
    }

    /// <summary>Open PRs. The issue is the first number in the branch name (fix/1531-short-name), as the worker brief asks.</summary>
    public async Task<IReadOnlyList<PrInfo>> OpenPrsAsync(string folder, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "pr", "list", "--state", "open", "--limit", "50",
            "--json", "number,url,headRefName,isDraft,mergeable,statusCheckRollup,body,closingIssuesReferences");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e =>
            {
                var mergeable = e.GetProperty("mergeable").GetString() ?? "UNKNOWN";
                return new PrInfo(
                    e.GetProperty("number").GetInt32(),
                    e.GetProperty("url").GetString() ?? "",
                    IssueOf(e),
                    e.GetProperty("isDraft").GetBoolean(),
                    mergeable,
                    PrStatus.From(mergeable, e.GetProperty("statusCheckRollup").Clone()),
                    e.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    PrStatus.AnyRunning(e.GetProperty("statusCheckRollup")));
            })
            .ToList();
    }

    /// <summary>Every open issue that carries the in-progress label, not limited to the dashboard's list.</summary>
    public async Task<IReadOnlySet<int>> InProgressIssuesAsync(string folder, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "issue", "list", "--state", "open", "--label", IssueLabels.InProgress,
            "--limit", "300", "--json", "number");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("number").GetInt32()).ToHashSet();
    }

    /// <summary>Closed issues that still carry the in-progress label.</summary>
    public async Task<IReadOnlySet<int>> ClosedInProgressIssuesAsync(string folder, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "issue", "list", "--state", "closed", "--label", IssueLabels.InProgress,
            "--limit", "300", "--json", "number");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("number").GetInt32()).ToHashSet();
    }

    /// <summary>Open issues carrying the given label. A label that does not exist yet has no issues, so this does not fail for that reason.</summary>
    public async Task<IReadOnlySet<int>> IssuesWithLabelAsync(string folder, string label, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "issue", "list", "--state", "open", "--label", label,
            "--limit", "300", "--json", "number");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("number").GetInt32()).ToHashSet();
    }

    /// <summary>The newest merged or closed PR per issue. Used to free the in-progress label and to show finished work.</summary>
    public async Task<IReadOnlyDictionary<int, PrInfo>> FinishedPrsByIssueAsync(string folder, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "pr", "list", "--state", "all", "--limit", "100",
            "--json", "number,url,headRefName,state,closingIssuesReferences");
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, PrInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var state = e.GetProperty("state").GetString();
            if (state is not ("MERGED" or "CLOSED")) continue;
            var issue = IssueOf(e);
            // gh lists the newest PR first, so the first one seen per issue is the one to show.
            if (issue is null || result.ContainsKey(issue.Value)) continue;
            result[issue.Value] = new PrInfo(
                e.GetProperty("number").GetInt32(),
                e.GetProperty("url").GetString() ?? "",
                issue,
                false,
                "UNKNOWN",
                state == "MERGED" ? "merged" : "closed");
        }
        return result;
    }

    /// <summary>How many issues were closed after the given moment. The search is by date, so the exact cut is made on closedAt.</summary>
    public async Task<int> ClosedSinceAsync(string folder, DateTimeOffset since, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "issue", "list", "--state", "closed", "--search", $"closed:>={since.UtcDateTime:yyyy-MM-dd}",
            "--limit", "300", "--json", "closedAt");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Count(e => e.GetProperty("closedAt").GetDateTimeOffset() > since);
    }

    /// <summary>The raw gh JSON of an issue with its comments, oldest first.</summary>
    public async Task<JsonDocument> IssueCommentsAsync(string folder, int issue, CancellationToken ct) =>
        JsonDocument.Parse(await RunAsync(folder, ct, "issue", "view", issue.ToString(), "--json", "comments"));

    /// <summary>Every open issue with the label, with its first 100 comments, by number. One call covers all of them.</summary>
    public async Task<IReadOnlyDictionary<int, JsonElement>> IssuesWithCommentsAsync(string folder, string label, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "issue", "list", "--state", "open", "--label", label,
            "--limit", "300", "--json", "number,comments");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("number").GetInt32(), e => e.Clone());
    }

    /// <summary>When each label of the issue was added and removed.</summary>
    public async Task<IReadOnlyList<(string Event, string Label, DateTimeOffset At)>> LabelHistoryAsync(string folder, string repo, int issue, CancellationToken ct)
    {
        var lines = await RunAsync(folder, ct, "api", $"repos/{repo}/issues/{issue}/events", "--paginate",
            "--jq", ".[] | select(.label) | [.event, .label.name, .created_at] | @tsv");
        return lines.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Select(p => (p[0], p[1], DateTimeOffset.Parse(p[2], CultureInfo.InvariantCulture)))
            .ToList();
    }

    /// <summary>Whether an issue or PR of the repo is still open. The issues API answers for both, and tells a merged PR from a closed one.</summary>
    public async Task<BlockerState> BlockerStateAsync(string folder, string repo, int number, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await RunAsync(folder, ct, "api", $"repos/{repo}/issues/{number}"));
        return ReadBlockerState(doc.RootElement);
    }

    public static BlockerState ReadBlockerState(JsonElement issue)
    {
        if (issue.GetProperty("state").GetString() == "open") return BlockerState.Open;
        // An issue closed by a merged PR has the reason "completed". A PR itself carries merged_at.
        var completed = issue.TryGetProperty("state_reason", out var reason) && reason.GetString() == "completed";
        var merged = issue.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object
            && pr.TryGetProperty("merged_at", out var at) && at.ValueKind == JsonValueKind.String;
        return completed || merged ? BlockerState.Done : BlockerState.Closed;
    }

    /// <summary>True while the issue is open. Used for a session whose issue is not in the open-issue list.</summary>
    public async Task<bool> IssueIsOpenAsync(string folder, int issue, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "issue", "view", issue.ToString(), "--json", "state");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("state").GetString() == "OPEN";
    }

    /// <summary>
    /// The cloud session a worker names in its PR description (the "Generated by Claude Code" link), as a cse_ id, or null.
    /// Lets the agent pick a session up again after it lost track of it.
    /// </summary>
    public static string? SessionFromPrBody(string body)
    {
        var m = System.Text.RegularExpressions.Regex.Matches(body, @"claude\.ai/code/(?:session_|cse_)([A-Za-z0-9]+)");
        return m.Count == 0 ? null : "cse_" + m[^1].Groups[1].Value;
    }

    /// <summary>
    /// When the newest comment or review by someone other than <paramref name="own"/> was written on the PR, or null.
    /// Replies to review threads belong to a review, so a review by someone else covers inline comments too.
    /// </summary>
    public async Task<DateTimeOffset?> LatestCommentAsync(string folder, int pr, ISet<string> own, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "pr", "view", pr.ToString(), "--json", "comments,reviews");
        using var doc = JsonDocument.Parse(json);
        return LatestOutsideComment(doc.RootElement, own);
    }

    public static DateTimeOffset? LatestOutsideComment(JsonElement pr, ISet<string> own)
    {
        DateTimeOffset? latest = null;
        foreach (var (list, stamp) in new[] { ("comments", "createdAt"), ("reviews", "submittedAt") })
        {
            if (!pr.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                var login = item.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var l) ? l.GetString() : null;
                if (login is null || own.Contains(login)) continue;
                if (!item.TryGetProperty(stamp, out var t) || !t.TryGetDateTimeOffset(out var at)) continue;
                if (latest is null || at > latest) latest = at;
            }
        }
        return latest;
    }

    /// <summary>The login the gh CLI is signed in as.</summary>
    public async Task<string> CurrentLoginAsync(CancellationToken ct) =>
        (await RunAsync(".", ct, "api", "user", "--jq", ".login")).Trim();

    /// <summary>
    /// The issue a PR works on. The branch number (fix/1531-name) when the PR closes that issue or closes nothing, else
    /// the first issue it closes. A branch such as claude/clever-curie-2z9y6j has no real issue number, so the closing
    /// reference ("Closes #N") decides there.
    /// </summary>
    public static int? IssueOf(JsonElement pr)
    {
        var fromBranch = IssueFromBranch(pr.GetProperty("headRefName").GetString() ?? "");
        var closing = new List<int>();
        if (pr.TryGetProperty("closingIssuesReferences", out var refs) && refs.ValueKind == JsonValueKind.Array)
            foreach (var r in refs.EnumerateArray())
                if (r.TryGetProperty("number", out var n) && n.TryGetInt32(out var number)) closing.Add(number);
        if (closing.Count == 0 || (fromBranch is int b && closing.Contains(b))) return fromBranch;
        return closing[0];
    }

    /// <summary>The issue number in a branch name such as fix/1531-short-name, or null.</summary>
    public static int? IssueFromBranch(string branch)
    {
        var digits = new string(branch.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var issue) ? issue : null;
    }

    private static async Task<string> RunAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true };
        GhEnv.Apply(psi, cwd);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("gh did not start");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"gh {args[0]} {args[1]} failed: {stderr.Trim()}");
        return stdout;
    }
}
