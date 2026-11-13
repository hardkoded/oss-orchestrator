using System.Diagnostics;
using System.Text.Json;

namespace OssOrchestrator.Agent;

/// <summary>An open PR. Issue is parsed from the branch name, when it carries one.</summary>
public sealed record PrInfo(int Number, string Url, int? Issue, bool IsDraft, string Mergeable, string Status);

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

        return failed ? "failed" : running ? "building" : "ready";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Reads issues and PRs through the gh CLI (the user's own login, nothing stored here).</summary>
public sealed class GitHub
{
    public async Task<IReadOnlyList<IssueInfo>> OpenIssuesAsync(RepoSettings repo, CancellationToken ct)
    {
        var json = await RunAsync(repo.Folder, ct, "issue", "list", "--state", "open", "--limit", "30",
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
            "--json", "number,url,headRefName,isDraft,mergeable,statusCheckRollup");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e =>
            {
                var branch = e.GetProperty("headRefName").GetString() ?? "";
                var mergeable = e.GetProperty("mergeable").GetString() ?? "UNKNOWN";
                return new PrInfo(
                    e.GetProperty("number").GetInt32(),
                    e.GetProperty("url").GetString() ?? "",
                    IssueFromBranch(branch),
                    e.GetProperty("isDraft").GetBoolean(),
                    mergeable,
                    PrStatus.From(mergeable, e.GetProperty("statusCheckRollup").Clone()));
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
            "--json", "number,url,headRefName,state");
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, PrInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var state = e.GetProperty("state").GetString();
            if (state is not ("MERGED" or "CLOSED")) continue;
            var issue = IssueFromBranch(e.GetProperty("headRefName").GetString() ?? "");
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

    /// <summary>True while the issue is open. Used for a session whose issue is not in the open-issue list.</summary>
    public async Task<bool> IssueIsOpenAsync(string folder, int issue, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "issue", "view", issue.ToString(), "--json", "state");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("state").GetString() == "OPEN";
    }

    /// <summary>The issue number in a branch name such as fix/1531-short-name, or null.</summary>
    public static int? IssueFromBranch(string branch)
    {
        var digits = new string(branch.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var issue) ? issue : null;
    }

    private static async Task<string> RunAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("gh") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("gh did not start");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"gh {args[0]} {args[1]} failed: {stderr.Trim()}");
        return stdout;
    }
}
