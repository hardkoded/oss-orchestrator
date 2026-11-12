using System.Diagnostics;

namespace OssOrchestrator.Agent;

/// <summary>Starts Claude sessions in Herdr. DryRun only logs what it would do.</summary>
public sealed class Herdr(bool dryRun, TextWriter log)
{
    private const string HerdrApp = @"C:\Users\Mabl-Mobl\AppData\Local\Programs\herdr-app\herdr-app.exe";

    public async Task LaunchAsync(RepoSettings repo, int issue, string task, CancellationToken ct)
    {
        var name = $"orc #{issue}";
        if (dryRun)
        {
            await log.WriteLineAsync($"[dry-run] would launch {repo.Repo} #{issue}");
            return;
        }
        var args = new List<string> { "do", "space.new", "--name", repo.Repo, "--folder", repo.Folder, "--tab", name,
            "--argv", "[\"claude\",\"-n\",\"issue-" + issue + "\"]", "--task", task };
        var psi = new ProcessStartInfo(HerdrApp) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("herdr-app did not start");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"herdr-app failed for #{issue}: {await p.StandardError.ReadToEndAsync(ct)}");
    }
}
