using System.Diagnostics;
using System.Text.Json;

namespace OssOrchestrator.Agent;

/// <summary>An open "orc #N" tab: its repo (the space name), issue, and the status of its worker.</summary>
public sealed record SessionInfo(string Repo, int Issue, string Status);

/// <summary>Starts Claude workers in Herdr: one tab per issue inside the repo's space. DryRun only logs.</summary>
public sealed class Herdr(bool dryRun, TextWriter log)
{
    private const string HerdrApp = @"C:\Users\Mabl-Mobl\AppData\Local\Programs\herdr-app\herdr-app.exe";
    private const string Prefix = "orc #";

    /// <summary>Every open "orc #N" tab. The global limit counts these.</summary>
    public async Task<List<SessionInfo>> ListSessionsAsync(CancellationToken ct)
    {
        using var listing = JsonDocument.Parse(await RunAsync(ct, "ls"));
        var result = new List<SessionInfo>();
        foreach (var space in listing.RootElement.GetProperty("spaces").EnumerateArray())
        {
            var repo = space.GetProperty("name").GetString() ?? "";
            foreach (var tab in space.GetProperty("tabs").EnumerateArray())
            {
                var name = tab.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                if (!int.TryParse(name[Prefix.Length..], out var issue)) continue;
                var status = tab.GetProperty("terminals").EnumerateArray()
                    .Select(t => t.TryGetProperty("status", out var s) ? s.GetString() : null)
                    .FirstOrDefault(s => s is not null) ?? "unknown";
                result.Add(new SessionInfo(repo, issue, status));
            }
        }
        return result;
    }

    public async Task LaunchAsync(string repo, string folder, int issue, string task, CancellationToken ct)
    {
        var tab = $"{Prefix}{issue}";
        var argv = $"[\"claude\",\"-n\",\"issue-{issue}\"]";
        if (dryRun)
        {
            await log.WriteLineAsync($"[dry-run] would launch {repo} #{issue}");
            return;
        }

        // Reuse the repo's space when it is already open, so each repo keeps one space with one tab per issue.
        using var listing = JsonDocument.Parse(await RunAsync(ct, "ls"));
        var space = listing.RootElement.GetProperty("spaces").EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("name").GetString() == repo);

        if (space.ValueKind == JsonValueKind.Object)
            await RunAsync(ct, "do", "tab.new", "--space", space.GetProperty("id").GetString()!, "--name", tab,
                "--folder", folder, "--argv", argv, "--task", task);
        else
            await RunAsync(ct, "do", "space.new", "--name", repo, "--folder", folder, "--tab", tab,
                "--argv", argv, "--task", task);
    }

    private static async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(HerdrApp) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("herdr-app did not start");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"herdr-app {args[0]} failed: {stderr.Trim()}{stdout.Trim()}");
        return stdout;
    }
}
