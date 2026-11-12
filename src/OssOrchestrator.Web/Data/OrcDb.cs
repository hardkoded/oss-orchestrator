using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace OssOrchestrator.Web.Data;

// Same document shapes the agent writes (see src/OssOrchestrator.Agent/DbStore.cs). Keep them in sync.

public sealed class RepoDoc
{
    public string Repo { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool Paused { get; set; }
    public bool Auto { get; set; }
    public List<int> Ignored { get; set; } = [];
}

public sealed class ConfigDoc
{
    public string Id { get; set; } = "config";
    public int MaxTotal { get; set; } = 6;
    public string Mode { get; set; } = "local";
    public List<RepoDoc> Repos { get; set; } = [];
}

public sealed class CommandDoc
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Repo { get; set; }
    public int? Issue { get; set; }
    public object? Value { get; set; }
    public bool Processed { get; set; }
    public string? Result { get; set; }
    public string? By { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
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
    public string? CloudSessionId { get; set; }
    public string? CloudSessionUrl { get; set; }
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

public sealed class OrcDb(Container config, Container commands, Container state)
{
    /// <summary>Uses the app's own identity (managed identity in Azure, your az login locally). No keys.</summary>
    public static OrcDb Create(string endpoint)
    {
        var client = new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        });
        var db = client.GetDatabase("orc");
        return new OrcDb(db.GetContainer("config"), db.GetContainer("commands"), db.GetContainer("state"));
    }

    public async Task<StateDoc?> ReadStateAsync() => await ReadOrNull<StateDoc>(state, "current");

    public async Task<ConfigDoc?> ReadConfigAsync() => await ReadOrNull<ConfigDoc>(config, "config");

    public async Task<List<CommandDoc>> RecentCommandsAsync(int take = 10)
    {
        var result = new List<CommandDoc>();
        var query = commands.GetItemQueryIterator<CommandDoc>(
            new QueryDefinition($"SELECT TOP {take} * FROM c ORDER BY c.createdAt DESC"));
        while (query.HasMoreResults) result.AddRange(await query.ReadNextAsync());
        return result;
    }

    public Task QueueAsync(CommandDoc cmd) => commands.UpsertItemAsync(cmd, new PartitionKey(cmd.Id));

    private static async Task<T?> ReadOrNull<T>(Container container, string id) where T : class
    {
        try
        {
            return (await container.ReadItemAsync<T>(id, new PartitionKey(id))).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
