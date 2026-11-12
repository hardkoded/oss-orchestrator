using System.Diagnostics;

namespace OssOrchestrator.Agent;

/// <summary>Marks an issue as taken (assigned to the user) when a session starts, so it is not offered again.</summary>
public static class IssueAssigner
{
    public static async Task AssignSelfAsync(string folder, int issue, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "issue", "edit", issue.ToString(), "--add-assignee", "@me" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("gh did not start");
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"could not assign #{issue}: {stderr.Trim()}");
    }
}
