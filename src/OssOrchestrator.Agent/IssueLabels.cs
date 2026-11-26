using System.Diagnostics;

namespace OssOrchestrator.Agent;

/// <summary>Labels the orchestrator reads and writes on issues.</summary>
public static class IssueLabels
{
    /// <summary>Set when a session starts on the issue, so it is not offered again.</summary>
    public const string InProgress = "in-progress";

    /// <summary>Set by a cloud worker when it has asked a question on the issue. The worker removes it once it has the answer.</summary>
    public const string NeedsAnswer = "needs-answer";

    /// <summary>Set by a cloud worker while it waits for another issue or PR of the repo, named in a blocked-by comment. The agent removes it when the blocker is merged or closed.</summary>
    public const string WaitingOn = "waiting-on";

    /// <summary>Set by a person. The issue is shown on the dashboard but the orchestrator never takes it.</summary>
    public const string Blocked = "blocked";

    /// <summary>Adds the in-progress label. Labels are used instead of assignees so no one is shown as the owner.</summary>
    public static Task MarkInProgressAsync(string folder, int issue, CancellationToken ct) =>
        EditAsync(folder, issue, "--add-label", InProgress, ct);

    /// <summary>Removes the in-progress label once the issue's PR is merged or closed.</summary>
    public static Task RemoveInProgressAsync(string folder, int issue, CancellationToken ct) =>
        EditAsync(folder, issue, "--remove-label", InProgress, ct);

    /// <summary>Adds the waiting-on label for a worker that named its blockers without it. A repo may not have the label yet.</summary>
    public static async Task MarkWaitingOnAsync(string folder, int issue, CancellationToken ct)
    {
        await RunAsync(folder, ct, "label", "create", WaitingOn);   // fails when the label is already there
        await EditAsync(folder, issue, "--add-label", WaitingOn, ct);
    }

    /// <summary>Removes the needs-answer label of a worker that waits on blockers now, so its question is over.</summary>
    public static Task RemoveNeedsAnswerAsync(string folder, int issue, CancellationToken ct) =>
        EditAsync(folder, issue, "--remove-label", NeedsAnswer, ct);

    /// <summary>Removes the waiting-on label once the worker has been told its blocker is done.</summary>
    public static Task RemoveWaitingOnAsync(string folder, int issue, CancellationToken ct) =>
        EditAsync(folder, issue, "--remove-label", WaitingOn, ct);

    /// <summary>Adds the blocked label, set by a dashboard Block, so the orchestrator never takes the issue again.</summary>
    public static Task MarkBlockedAsync(string folder, int issue, CancellationToken ct) =>
        EditAsync(folder, issue, "--add-label", Blocked, ct);

    /// <summary>Removes the blocked label, set by a dashboard Unblock, so the issue can be taken again.</summary>
    public static Task RemoveBlockedAsync(string folder, int issue, CancellationToken ct) =>
        EditAsync(folder, issue, "--remove-label", Blocked, ct);

    private static async Task EditAsync(string folder, int issue, string flag, string label, CancellationToken ct)
    {
        var (exitCode, stderr) = await RunAsync(folder, ct, "issue", "edit", issue.ToString(), flag, label);
        if (exitCode != 0) throw new InvalidOperationException($"could not change label on #{issue}: {stderr.Trim()}");
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(string folder, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true };
        GhEnv.Apply(psi, folder);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("gh did not start");
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, stderr);
    }
}
