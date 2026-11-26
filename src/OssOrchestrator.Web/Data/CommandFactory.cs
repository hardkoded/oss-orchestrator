namespace OssOrchestrator.Web.Data;

/// <summary>Builds the commands the pages may queue. The agent applies them; the page never talks to GitHub.</summary>
public static class CommandFactory
{
    private static readonly string[] Allowed =
        ["pause", "resume", "pause_all", "resume_all", "auto", "ignore", "restore", "unblock", "block_session", "refresh", "limit", "sessions"];

    public static (CommandDoc? Command, string? Error) Build(string type, string? repo, int? issue, string? value, string? by)
    {
        if (!Allowed.Contains(type)) return (null, "That command is not allowed.");

        object? parsed = null;
        if (type == "auto") parsed = value == "true";
        if (type is "limit" or "sessions")
        {
            if (!int.TryParse(value, out var limit) || limit is < 1 or > 20) return (null, $"{(type == "limit" ? "Work slots" : "Max sessions")} must be between 1 and 20.");
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

    /// <summary>
    /// The commands behind one dashboard button, written as "type|repo|issue|value". The type "take" is the repo's
    /// "Take new issues" switch: turning it on also resumes a paused repo, because the agent needs both.
    /// </summary>
    public static (IReadOnlyList<CommandDoc> Commands, string? Error) FromButton(string? button, ConfigDoc? config, string? by)
    {
        var parts = (button ?? "").Split('|');
        var type = parts[0];
        var repo = Part(1);
        var issue = int.TryParse(Part(2), out var n) ? n : (int?)null;
        var value = Part(3);

        var steps = new List<(string Type, string? Value)> { (type, value) };
        if (type == "take")
        {
            var current = config?.Repos.FirstOrDefault(r => r.Repo == repo);
            if (current is null) return ([], "Unknown repo.");
            steps = [];
            if (value != "true") steps.Add(("auto", "false"));
            else
            {
                if (current.Paused) steps.Add(("resume", null));
                if (!current.Auto) steps.Add(("auto", "true"));
            }
        }

        var commands = new List<CommandDoc>();
        foreach (var step in steps)
        {
            var (cmd, error) = Build(step.Type, repo, issue, step.Value, by);
            if (cmd is null) return ([], error);
            commands.Add(cmd);
        }
        return (commands, null);

        string? Part(int i) => parts.Length > i && parts[i].Length > 0 ? parts[i] : null;
    }
}
