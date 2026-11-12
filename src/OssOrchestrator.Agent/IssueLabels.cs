using System.Diagnostics;

namespace OssOrchestrator.Agent;

/// <summary>Labels the orchestrator reads and writes on issues.</summary>
public static class IssueLabels
{
    /// <summary>Set when a session starts on the issue, so it is not offered again.</summary>
    public const string InProgress = "in-progress";

    /// <summary>Set by a person. The issue is shown on the dashboard but the orchestrator never takes it.</summary>
    public const string Blocked = "blocked";

    /// <summary>Adds the in-progress label. Labels are used instead of assignees so no one is shown as the owner.</summary>
    public static async Task MarkInProgressAsync(string folder, int issue, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "issue", "edit", issue.ToString(), "--add-label", InProgress }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("gh did not start");
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"could not label #{issue}: {stderr.Trim()}");
    }
}
