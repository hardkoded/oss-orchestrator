using System.Diagnostics;
using System.Text.Json;

namespace OssOrchestrator.Agent;

/// <summary>An open "orc #N" tab: its repo (the space name), issue, and the status of its worker.</summary>
public sealed record SessionInfo(string Repo, int Issue, string Status, string? Terminal = null,
    string? CloudSessionId = null, string? CloudSessionUrl = null);

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
                var terminal = tab.GetProperty("terminals").EnumerateArray()
                    .Select(t => t.TryGetProperty("terminal", out var s) ? s.GetString() : null)
                    .FirstOrDefault(s => s is not null);
                result.Add(new SessionInfo(repo, issue, status, terminal));
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

    /// <summary>Closes the "orc #N" tab of a repo and ends its terminal, so the worker is released. Nothing to do if it is gone.</summary>
    public async Task CloseSessionAsync(string repo, int issue, CancellationToken ct)
    {
        var tabName = $"{Prefix}{issue}";
        using var listing = JsonDocument.Parse(await RunAsync(ct, "ls"));
        var tabId = listing.RootElement.GetProperty("spaces").EnumerateArray()
            .Where(s => s.GetProperty("name").GetString() == repo)
            .SelectMany(s => s.GetProperty("tabs").EnumerateArray())
            .Where(t => t.GetProperty("name").GetString() == tabName)
            .Select(t => t.GetProperty("id").GetString())
            .FirstOrDefault();
        if (tabId is null) return;
        await RunAsync(ct, "do", "tab.close", "--id", tabId, "--confirmed", "true");
    }

    /// <summary>
    /// Types a message into a worker's terminal (hc send), only while the worker is idle so it never interrupts work.
    /// Returns false when the worker was busy or the send failed.
    /// </summary>
    public async Task<bool> SendAsync(string terminal, string text, CancellationToken ct)
    {
        if (dryRun)
        {
            await log.WriteLineAsync($"[dry-run] would send to {terminal}: {text}");
            return true;
        }
        var psi = new ProcessStartInfo("hc") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "send", "--if", "idle", terminal, text }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("hc did not start");
        await p.StandardOutput.ReadToEndAsync(ct);
        await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0;
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
