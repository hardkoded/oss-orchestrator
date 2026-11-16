using System.Diagnostics;

namespace OssOrchestrator.Agent;

/// <summary>A cloud session started for an issue. Id and Url are the claude.ai session the routine returned.</summary>
public sealed class CloudDoc
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Starts cloud sessions by firing the routine, and sends messages to running ones.</summary>
public static class CloudSessions
{
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

    /// <summary>
    /// Sends a message to a running cloud session (claude -p --cloud). The CLI takes the cse_ form of the id,
    /// which shares its suffix with the session_ id the dashboard stores.
    /// </summary>
    public static async Task<bool> SendAsync(string sessionId, string text, CancellationToken ct)
    {
        var id = sessionId.StartsWith("session_", StringComparison.Ordinal) ? "cse_" + sessionId["session_".Length..] : sessionId;
        var psi = new ProcessStartInfo("claude") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in new[] { "-p", text, "--cloud", id }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("claude did not start");
        p.StandardInput.Close();
        await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode == 0) return true;
        var error = stderr.Trim();
        throw error.Contains("Session not found", StringComparison.OrdinalIgnoreCase)
            ? new CloudSessionGoneException(error)
            : new InvalidOperationException(error);
    }
}

/// <summary>The cloud session no longer exists, so a message cannot reach it.</summary>
public sealed class CloudSessionGoneException(string message) : Exception(message);
