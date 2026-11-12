using System.Diagnostics;
using System.Text.Json;

namespace OssOrchestrator.Agent;

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
