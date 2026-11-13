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
        foreach (var a in new[] { "run", "--name", name, "claude", "--settings", WriteWorkerSettings(), "--allowedTools", "mcp__claude-code-remote__add_repo,RemoteTrigger", "--cloud", task }) psi.ArgumentList.Add(a);
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

    /// <summary>The routine that holds the four repos and the worker prompt. Its URL and trigger are fixed.</summary>
    private const string RoutineFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_01RwJHyFQQigx4yDhVwN7Q93/fire";

    /// <summary>
    /// Starts a session by firing the routine. The routine clones all repos and holds the worker prompt, so the
    /// payload only names the issue. The token comes from ORC_ROUTINE_TOKEN in the user environment, never from a file.
    /// </summary>
    public static async Task<CloudDoc> LaunchRoutineAsync(string repo, int issue, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("ORC_ROUTINE_TOKEN", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("ORC_ROUTINE_TOKEN")
            ?? throw new InvalidOperationException("ORC_ROUTINE_TOKEN is not set");

        using var http = new System.Net.Http.HttpClient();
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, RoutineFireUrl);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "experimental-cc-routine-2026-04-01");
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = System.Net.Http.Json.JsonContent.Create(new { text = $"Repo: {repo}. Issue #{issue}." });

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"routine fire for {repo}#{issue} failed: {(int)res.StatusCode} {body}");

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("claude_code_session_id").GetString() ?? "";
        var url = doc.RootElement.GetProperty("claude_code_session_url").GetString() ?? "";
        return new CloudDoc { Id = id, Url = url, CreatedAt = DateTimeOffset.UtcNow };
    }
}
