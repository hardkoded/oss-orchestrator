using System.Text.Json;

namespace OssOrchestrator.Agent;

/// <summary>Applies one dashboard command to the config document. Command fields are data; they are never run.</summary>
public static class Applier
{
    public static string Apply(ConfigDoc config, CommandDoc cmd)
    {
        switch (cmd.Type)
        {
            case "pause_all":
                foreach (var r in config.Repos) r.Paused = true;
                return "paused all";
            case "resume_all":
                foreach (var r in config.Repos) r.Paused = false;
                return "resumed all";
            case "limit":
                if (cmd.Value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var limit) && limit is >= 1 and <= 20)
                {
                    config.MaxTotal = limit;
                    return $"limit set to {limit}";
                }
                return "limit must be between 1 and 20";
            case "pause":
            case "resume":
            case "auto":
            case "ignore":
            case "restore":
            {
                var repo = config.Repos.FirstOrDefault(r => r.Repo == cmd.Repo);
                if (repo is null) return $"unknown repo {cmd.Repo}";
                switch (cmd.Type)
                {
                    case "pause": repo.Paused = true; return $"paused {repo.Repo}";
                    case "resume": repo.Paused = false; return $"resumed {repo.Repo}";
                    case "auto":
                        repo.Auto = cmd.Value is { ValueKind: JsonValueKind.True };
                        return $"auto {(repo.Auto ? "on" : "off")} for {repo.Repo}";
                }
                if (cmd.Issue is not int n) return "missing issue number";
                if (cmd.Type == "ignore")
                {
                    if (!repo.Ignored.Contains(n)) repo.Ignored.Add(n);
                    return $"ignored {repo.Repo} #{n}";
                }
                repo.Ignored.Remove(n);
                return $"restored {repo.Repo} #{n}";
            }
            case "refresh":
                // Nothing to change here: the pass that applies this command reads GitHub, Herdr and state again.
                return "refreshed: full pass run";
            default:
                return $"unknown command {cmd.Type}";
        }
    }
}
