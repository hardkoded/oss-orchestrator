using System.Diagnostics;
using System.Text.Json;

namespace OssOrchestrator.Agent;

/// <summary>An open PR. Issue is parsed from the branch name, when it carries one.</summary>
public sealed record PrInfo(int Number, string Url, int? Issue, bool IsDraft, string Mergeable);

/// <summary>Reads issues and PRs through the gh CLI (the user's own login, nothing stored here).</summary>
public sealed class GitHub
{
    public async Task<IReadOnlyList<IssueInfo>> OpenIssuesAsync(RepoSettings repo, CancellationToken ct)
    {
        var json = await RunAsync(repo.Folder, ct, "issue", "list", "--state", "open", "--limit", "30",
            "--json", "number,title,assignees");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new IssueInfo(
                e.GetProperty("number").GetInt32(),
                e.GetProperty("title").GetString() ?? "",
                e.GetProperty("assignees").GetArrayLength() > 0))
            .ToList();
    }

    /// <summary>Open PRs. The issue is the first number in the branch name (fix/1531-short-name), as the worker brief asks.</summary>
    public async Task<IReadOnlyList<PrInfo>> OpenPrsAsync(string folder, CancellationToken ct)
    {
        var json = await RunAsync(folder, ct, "pr", "list", "--state", "open", "--limit", "50",
            "--json", "number,url,headRefName,isDraft,mergeable");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e =>
            {
                var branch = e.GetProperty("headRefName").GetString() ?? "";
                var digits = new string(branch.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
                return new PrInfo(
                    e.GetProperty("number").GetInt32(),
                    e.GetProperty("url").GetString() ?? "",
                    int.TryParse(digits, out var issue) ? issue : null,
                    e.GetProperty("isDraft").GetBoolean(),
                    e.GetProperty("mergeable").GetString() ?? "UNKNOWN");
            })
            .ToList();
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
