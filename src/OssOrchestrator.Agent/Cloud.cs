using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OssOrchestrator.Agent;

/// <summary>A cloud session started for an issue. Id and Url are the claude.ai link the CLI printed.</summary>
public sealed class CloudDoc
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Starts a Claude cloud session with `claude --cloud`. The CLI refuses to run without a terminal, so it runs inside
/// an hc terminal, and the link is read from what it prints.
/// </summary>
public static partial class CloudSessions
{
    [GeneratedRegex(@"View: (https://claude\.ai/code/(session_[A-Za-z0-9]+))")]
    private static partial Regex ViewLine();

    /// <summary>
    /// The permissions every worker needs. Repo-level .claude/settings.json did not reach cloud sessions, so they are
    /// passed to each launch with --settings.
    /// </summary>
    private const string WorkerSettingsJson = """
        {"permissions":{"allow":["RemoteTrigger","mcp__claude-code-remote__add_repo"]}}
        """;

    /// <summary>Writes the worker settings file and returns its path. The path has no spaces, so it is safe on the command line.</summary>
    private static string WriteWorkerSettings()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oss-orchestrator");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "worker-settings.json");
        File.WriteAllText(path, WorkerSettingsJson.Trim());
        return path;
    }

    public static async Task<CloudDoc> LaunchAsync(string repo, string folder, int issue, string task, CancellationToken ct)
    {
        var name = $"orc-{issue}-{DateTime.UtcNow:HHmmss}";
        var psi = new ProcessStartInfo("hc") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "run", "--name", name, "claude", "--settings", WriteWorkerSettings(), "--cloud", task }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("hc did not start");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var match = ViewLine().Match(stdout);
        if (p.ExitCode != 0 || !match.Success)
            throw new InvalidOperationException($"no cloud session for {repo}#{issue}: {stdout.Trim()} {stderr.Trim()}".Trim());

        // The finished terminal is only history. Removing it keeps the terminal list short; a failure here is harmless.
        try
        {
            var rmInfo = new ProcessStartInfo("hc") { RedirectStandardOutput = true, RedirectStandardError = true };
            rmInfo.ArgumentList.Add("rm");
            rmInfo.ArgumentList.Add(name);
            using var rm = Process.Start(rmInfo);
            if (rm is not null) await rm.WaitForExitAsync(ct);
        }
        catch (Exception) { }

        return new CloudDoc { Id = match.Groups[2].Value, Url = match.Groups[1].Value, CreatedAt = DateTimeOffset.UtcNow };
    }
}
