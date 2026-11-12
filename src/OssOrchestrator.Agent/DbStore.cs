using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace OssOrchestrator.Agent;

// The database is the source of truth. The agent reads config and pending commands from it and writes
// results back. The Herdr panel's repos.json is only read once, to seed an empty database.

public sealed class RepoDoc
{
    public string Repo { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool Paused { get; set; }
    public bool Auto { get; set; }
    public List<int> Ignored { get; set; } = [];
    public Dictionary<string, long> Launched { get; set; } = [];
}

public sealed class ConfigDoc
{
    public string Id { get; set; } = "config";
    public int MaxTotal { get; set; } = 6;
    public List<RepoDoc> Repos { get; set; } = [];
}

public sealed class CommandDoc
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Repo { get; set; }
    public int? Issue { get; set; }
    public JsonElement? Value { get; set; }
    public bool Processed { get; set; }
    public string? Result { get; set; }
}

public sealed class StateDoc
{
    public string Id { get; set; } = "current";
    public DateTimeOffset At { get; set; }
    public bool DryRun { get; set; }
    public int Running { get; set; }
    public int MaxTotal { get; set; }
    public List<string> Planned { get; set; } = [];
    public List<RepoStatus> Repos { get; set; } = [];
    public List<SessionStatus> Sessions { get; set; } = [];
    public List<RepoIssues> Issues { get; set; } = [];
}

public sealed class RepoStatus
{
    public string Repo { get; set; } = "";
    public bool Paused { get; set; }
    public bool Auto { get; set; }
    public int OpenIssues { get; set; }
    public string? Error { get; set; }
}

/// <summary>A worker session open in Herdr, with the PR its issue opened (if any).</summary>
public sealed class SessionStatus
{
    public string Repo { get; set; } = "";
    public int Issue { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public int? PrNumber { get; set; }
    public string? PrUrl { get; set; }
    public bool PrConflict { get; set; }
    public bool PrDraft { get; set; }
    /// <summary>pending, building, ready, failed or conflicted.</summary>
    public string PrStatus { get; set; } = "pending";
}

public sealed class RepoIssues
{
    public string Repo { get; set; } = "";
    public List<IssueStatus> Items { get; set; } = [];
}

public sealed class IssueStatus
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public bool Assigned { get; set; }
    public bool InProgress { get; set; }
    public bool Blocked { get; set; }
    public bool Ignored { get; set; }
}

public sealed class DbStore(Container config, Container commands, Container state)
{
    public static DbStore Connect(string endpoint, string key)
    {
        var client = new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        });
        var db = client.GetDatabase("orc");
        return new DbStore(db.GetContainer("config"), db.GetContainer("commands"), db.GetContainer("state"));
    }

    public async Task<ConfigDoc> LoadConfigAsync(string panelReposPath)
    {
        try
        {
            return (await config.ReadItemAsync<ConfigDoc>("config", new PartitionKey("config"))).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // First run: seed the database once from the panel's repos.json, then the database owns it.
            var seeded = SeedFromPanel(panelReposPath);
            await config.UpsertItemAsync(seeded, new PartitionKey(seeded.Id));
            return seeded;
        }
    }

    public Task SaveConfigAsync(ConfigDoc doc) => config.UpsertItemAsync(doc, new PartitionKey(doc.Id));

    public async Task<List<CommandDoc>> PendingCommandsAsync()
    {
        var result = new List<CommandDoc>();
        var query = commands.GetItemQueryIterator<CommandDoc>(
            new QueryDefinition("SELECT * FROM c WHERE c.processed = false"));
        while (query.HasMoreResults)
            result.AddRange(await query.ReadNextAsync());
        return result;
    }

    public Task MarkCommandAsync(CommandDoc cmd) => commands.UpsertItemAsync(cmd, new PartitionKey(cmd.Id));

    public Task PutStateAsync(StateDoc snapshot) => state.UpsertItemAsync(snapshot, new PartitionKey(snapshot.Id));

    private static ConfigDoc SeedFromPanel(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var seeded = new ConfigDoc { MaxTotal = root.TryGetProperty("maxTotal", out var m) ? m.GetInt32() : 6 };
        foreach (var r in root.GetProperty("repos").EnumerateArray())
        {
            var repo = new RepoDoc
            {
                Repo = r.GetProperty("repo").GetString() ?? "",
                Folder = r.GetProperty("folder").GetString() ?? "",
                Paused = r.TryGetProperty("paused", out var p) && p.GetBoolean(),
                Auto = r.TryGetProperty("auto", out var a) && a.GetBoolean(),
            };
            if (r.TryGetProperty("ignored", out var ign))
                repo.Ignored = ign.EnumerateArray().Select(x => x.GetInt32()).ToList();
            if (r.TryGetProperty("launched", out var l))
                foreach (var kv in l.EnumerateObject())
                    repo.Launched[kv.Name] = kv.Value.GetInt64();
            seeded.Repos.Add(repo);
        }
        return seeded;
    }
}
