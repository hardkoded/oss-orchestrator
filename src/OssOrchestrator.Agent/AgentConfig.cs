using System.Text.Json;

namespace OssOrchestrator.Agent;

/// <summary>Reads the panel's repos.json (read only). The agent keeps its own overrides in memory.</summary>
public sealed class AgentConfig
{
    public required string DataDir { get; init; }

    public int MaxTotal { get; set; } = 6;
    public List<RepoState> Repos { get; private set; } = [];

    public sealed class RepoState
    {
        public required string Repo { get; init; }
        public required string Folder { get; init; }
        public bool Paused { get; set; }
        public bool Auto { get; set; }
        public HashSet<int> Ignored { get; set; } = [];
        public Dictionary<string, long> Launched { get; set; } = [];
    }

    private sealed class PanelFile
    {
        public int MaxTotal { get; set; } = 6;
        public List<PanelRepo> Repos { get; set; } = [];
    }

    private sealed class PanelRepo
    {
        public string Repo { get; set; } = "";
        public string Folder { get; set; } = "";
        public bool Paused { get; set; }
        public bool Auto { get; set; }
        public List<int> Ignored { get; set; } = [];
        public Dictionary<string, long> Launched { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Re-reads repos.json each pass, keeping the agent's in-memory pause/auto/ignore changes for repos it knows.</summary>
    public void Load()
    {
        var panel = JsonSerializer.Deserialize<PanelFile>(File.ReadAllText(Path.Combine(DataDir, "repos.json")), Json)
                    ?? throw new InvalidDataException("repos.json is empty");
        MaxTotal = panel.MaxTotal;
        var previous = Repos.ToDictionary(r => r.Repo);
        Repos = panel.Repos.Select(r =>
        {
            var keep = previous.GetValueOrDefault(r.Repo);
            return new RepoState
            {
                Repo = r.Repo,
                Folder = r.Folder,
                Paused = keep?.Paused ?? r.Paused,
                Auto = keep?.Auto ?? r.Auto,
                Ignored = keep?.Ignored ?? r.Ignored.ToHashSet(),
                Launched = r.Launched,
            };
        }).ToList();
    }

    /// <summary>Sessions open right now, from the panel's state.json (0 if it is missing).</summary>
    public int RunningNow()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataDir, "state.json")));
            return doc.RootElement.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        }
        catch (IOException) { return 0; }
        catch (JsonException) { return 0; }
    }

    public RepoState? Find(string repo) => Repos.FirstOrDefault(r => r.Repo == repo);
}
