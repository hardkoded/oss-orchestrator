using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Web;

// GitHub calls this when something changes on a repo. We only record the event: the agent wakes on it and
// reads the real state from GitHub itself. GitHub sends no event when a PR becomes conflicted, so a push to the
// default branch stands in for it.
public static class Webhooks
{
    private static readonly HashSet<string> PullRequestActions =
        ["opened", "closed", "reopened", "synchronize", "ready_for_review", "converted_to_draft"];

    public static void MapGitHubWebhook(this WebApplication app) =>
        app.MapPost("/webhooks/github", async (HttpRequest request, OrcDb db, IConfiguration config, CancellationToken ct) =>
        {
            // Fail closed: without a secret nobody can be trusted.
            var secret = config["GitHub:WebhookSecret"];
            if (string.IsNullOrEmpty(secret)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            // The endpoint is public: refuse unsigned or oversized requests before buffering anything (GitHub caps a payload at 25 MB).
            if (!request.Headers.ContainsKey("X-Hub-Signature-256")) return Results.Unauthorized();
            if (request.ContentLength is null or > 25 * 1024 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            var body = buffer.ToArray();
            if (!IsValidSignature(body, request.Headers["X-Hub-Signature-256"], secret)) return Results.Unauthorized();

            var delivery = request.Headers["X-GitHub-Delivery"].ToString();
            if (delivery.Length == 0) return Results.BadRequest();

            JsonElement payload;
            try { payload = JsonSerializer.Deserialize<JsonElement>(body); }
            catch (JsonException) { return Results.BadRequest(); }

            EventDoc? doc;
            try { doc = ToEvent(delivery, request.Headers["X-GitHub-Event"].ToString(), payload); }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { return Results.BadRequest(); }
            if (doc is null) return Results.NoContent();
            await db.PutEventAsync(doc, ct);
            return Results.Accepted();
        }).AllowAnonymous();

    /// <summary>Checks the HMAC-SHA256 that GitHub puts in X-Hub-Signature-256 ("sha256=" + hex), in constant time.</summary>
    public static bool IsValidSignature(byte[] body, string? header, string secret)
    {
        if (string.IsNullOrEmpty(header)) return false;
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>The event to record, or null when the agent has no reason to wake for it.</summary>
    public static EventDoc? ToEvent(string delivery, string eventName, JsonElement payload)
    {
        if (!payload.TryGetProperty("repository", out var repository)
            || repository.GetProperty("full_name").GetString() is not { Length: > 0 } repo) return null;
        var action = payload.TryGetProperty("action", out var a) ? a.GetString() : null;

        int? number = null;
        switch (eventName)
        {
            case "issues" when action is "opened" or "closed" or "reopened":
                number = payload.GetProperty("issue").GetProperty("number").GetInt32();
                break;
            case "pull_request" when action is not null && PullRequestActions.Contains(action):
                number = payload.GetProperty("pull_request").GetProperty("number").GetInt32();
                break;
            case "workflow_run" or "check_suite" when action == "completed":
                break;
            // A reviewer's comment or review is something the agent answers.
            case "pull_request_review" or "pull_request_review_comment" or "issue_comment" when action is "created" or "submitted":
                break;
            case "push" when payload.TryGetProperty("ref", out var r)
                && r.GetString() == $"refs/heads/{repository.GetProperty("default_branch").GetString()}":
                break;
            default:
                return null;
        }
        return new EventDoc { Id = delivery, Repo = repo, Type = eventName, Action = action, Number = number, At = DateTimeOffset.UtcNow };
    }
}
