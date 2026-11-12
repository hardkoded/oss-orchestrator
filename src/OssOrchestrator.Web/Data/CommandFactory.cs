namespace OssOrchestrator.Web.Data;

/// <summary>Builds the commands the pages may queue. The agent applies them; the page never talks to GitHub.</summary>
public static class CommandFactory
{
    private static readonly string[] Allowed =
        ["pause", "resume", "pause_all", "resume_all", "auto", "ignore", "restore", "unblock", "refresh", "limit"];

    public static (CommandDoc? Command, string? Error) Build(string type, string? repo, int? issue, string? value, string? by)
    {
        if (!Allowed.Contains(type)) return (null, "That command is not allowed.");

        object? parsed = null;
        if (type == "auto") parsed = value == "true";
        if (type == "limit")
        {
            if (!int.TryParse(value, out var limit) || limit is < 1 or > 20) return (null, "Limit must be between 1 and 20.");
            parsed = limit;
        }

        var cmd = new CommandDoc
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Repo = repo,
            Issue = issue,
            Value = parsed,
            CreatedAt = DateTimeOffset.UtcNow,
            By = by,
        };
        return (cmd, null);
    }

    /// <summary>Only local page paths are accepted as a redirect target.</summary>
    public static string SafeReturn(string? returnTo) =>
        returnTo is "/Settings" ? "/Settings" : "/";
}
