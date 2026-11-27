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
    /// <summary>Limit on all sessions, freed ones included.</summary>
    public int MaxSessions { get; set; } = 20;
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
    public int Ready { get; set; }
    public int MaxTotal { get; set; }
    public List<string> Planned { get; set; } = [];
    public List<RepoStatus> Repos { get; set; } = [];
    public List<SessionStatus> Sessions { get; set; } = [];
    public List<RepoIssues> Issues { get; set; } = [];
    /// <summary>Issues closed in the last 24 hours, across all repos.</summary>
    public int ClosedLast24h { get; set; }
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
    /// <summary>The issues or PRs of the same repo a waiting worker is blocked by, in the worker's order. Finished ones are left out while another is open.</summary>
    public List<int> Blockers { get; set; } = [];
    public int? PrNumber { get; set; }
    public string? PrUrl { get; set; }
    public bool PrConflict { get; set; }
    public bool PrDraft { get; set; }
    /// <summary>pending, building, ready, failed or conflicted.</summary>
    public string PrStatus { get; set; } = "pending";
    public string? CloudSessionId { get; set; }
    public string? CloudSessionUrl { get; set; }
    /// <summary>When the agent launched the session. Null for one it did not launch.</summary>
    public DateTimeOffset? StartedAt { get; set; }
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

/// <summary>The totals of all repos at one moment, recorded by the agent.</summary>
public sealed class HistoryPoint
{
    public DateTimeOffset At { get; set; }
    public int Open { get; set; }
    /// <summary>PRs merged in the 24 hours before <see cref="At"/>.</summary>
    public int Merged24h { get; set; }
    /// <summary>All open sessions. Null for a backfilled point.</summary>
    public int? Sessions { get; set; }
    /// <summary>Sessions that use a work slot. Null for a backfilled point.</summary>
    public int? Running { get; set; }
}

/// <summary>The points of one UTC day, or the backfilled ones.</summary>
public sealed class HistoryDoc
{
    public string Id { get; set; } = "";
    public List<HistoryPoint> Points { get; set; } = [];
}

/// <summary>A GitHub event worth waking the agent for. The agent reads these from the change feed. Expires after 7 days.</summary>
public sealed class EventDoc
{
    /// <summary>The GitHub delivery id, so a redelivery overwrites instead of duplicating.</summary>
    public string Id { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Action { get; set; }
    public int? Number { get; set; }
    public DateTimeOffset At { get; set; }
    public int Ttl { get; set; } = 7 * 24 * 60 * 60;
}

public sealed class OrcDb(Container config, Container commands, Container state, Container events)
{
    /// <summary>Uses the app's own identity (managed identity in Azure, your az login locally). No keys.</summary>
    public static OrcDb Create(string endpoint)
    {
        var client = new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        });
        var db = client.GetDatabase("orc");
        return new OrcDb(db.GetContainer("config"), db.GetContainer("commands"), db.GetContainer("state"), db.GetContainer("events"));
    }

    public async Task<StateDoc?> ReadStateAsync() => await ReadOrNull<StateDoc>(state, "current");

    public async Task<ConfigDoc?> ReadConfigAsync() => await ReadOrNull<ConfigDoc>(config, "config");

    public Task<List<CommandDoc>> RecentCommandsAsync(int take = 10) =>
        QueryCommandsAsync(new QueryDefinition($"SELECT TOP {take} * FROM c ORDER BY c.createdAt DESC"));

    /// <summary>Commands whose effect the state cannot show yet: not processed, or newer than the agent's last pass.</summary>
    public Task<List<CommandDoc>> PendingCommandsAsync(DateTimeOffset lastPass) =>
        QueryCommandsAsync(new QueryDefinition("SELECT * FROM c WHERE c.processed = false OR c.createdAt > @lastPass")
            .WithParameter("@lastPass", lastPass));

    /// <summary>The history documents of the days from <paramref name="since"/> on. "history-backfill" sorts after the dates, so it comes too.</summary>
    public Task<List<HistoryDoc>> HistoryAsync(DateTimeOffset since) =>
        QueryAsync<HistoryDoc>(state, new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, 'history-') AND c.id >= @from")
            .WithParameter("@from", $"history-{since.UtcDateTime:yyyy-MM-dd}"));

    private Task<List<CommandDoc>> QueryCommandsAsync(QueryDefinition definition) => QueryAsync<CommandDoc>(commands, definition);

    private static async Task<List<T>> QueryAsync<T>(Container container, QueryDefinition definition)
    {
        var result = new List<T>();
        var query = container.GetItemQueryIterator<T>(definition);
        while (query.HasMoreResults) result.AddRange(await query.ReadNextAsync());
        return result;
    }

    public Task PutEventAsync(EventDoc doc, CancellationToken ct = default) => events.UpsertItemAsync(doc, new PartitionKey(doc.Repo), cancellationToken: ct);

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
