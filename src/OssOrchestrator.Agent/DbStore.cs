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
    /// <summary>Cloud sessions by issue number, while their issue is open and has no finished PR.</summary>
    public Dictionary<string, CloudDoc> Cloud { get; set; } = [];
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
    // Kept as written by the dashboard: the agent saves the whole document when it marks a command processed.
    public DateTimeOffset CreatedAt { get; set; }
    public string? By { get; set; }
}

public sealed class StateDoc
{
    public string Id { get; set; } = "current";
    public DateTimeOffset At { get; set; }
    public bool DryRun { get; set; }
    /// <summary>Sessions that count toward the limit.</summary>
    public int Running { get; set; }
    /// <summary>Sessions parked on a ready or building PR. They do not count toward the limit.</summary>
    public int Ready { get; set; }
    public int MaxTotal { get; set; }
    public List<string> Planned { get; set; } = [];
    public List<RepoStatus> Repos { get; set; } = [];
    public List<SessionStatus> Sessions { get; set; } = [];
    public List<RepoIssues> Issues { get; set; } = [];
    /// <summary>Issues closed in the last 24 hours, across all repos.</summary>
    public int ClosedLast24h { get; set; }
}

/// <summary>What each worker was last told about its PR, so a red PR is not repeated on every pass (the cloud job keeps no memory between runs).</summary>
public sealed class NudgeDoc
{
    public string Id { get; set; } = "nudges";
    public Dictionary<string, NudgeEntry> Items { get; set; } = [];
    /// <summary>"repo#issue" -> the newest comment by someone else that its worker was already told about.</summary>
    public Dictionary<string, DateTimeOffset> Comments { get; set; } = [];
}

public sealed class NudgeEntry
{
    public int Pr { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>A GitHub event the web app's webhook recorded (see OrcDb.EventDoc in the web project). Keep them in sync.</summary>
public sealed class EventDoc
{
    public string Repo { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Action { get; set; }
    public int? Number { get; set; }

    /// <summary>"owner/repo type action #number", for the log.</summary>
    public override string ToString() =>
        string.Join(' ', new[] { Repo, Type, Action, Number is int n ? $"#{n}" : null }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>Where the agent stopped reading the events change feed, so a restart neither replays nor loses events.</summary>
public sealed class FeedDoc
{
    public string Id { get; set; } = "feed";
    public string? Token { get; set; }
}

public sealed class RepoStatus
{
    public string Repo { get; set; } = "";
    public bool Paused { get; set; }
    public bool Auto { get; set; }
    public int OpenIssues { get; set; }
    public string? Error { get; set; }
}

/// <summary>A cloud worker session, with the PR its issue opened (if any).</summary>
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
    /// <summary>The cloud session's id and link.</summary>
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

public sealed class DbStore(Container config, Container commands, Container state, Container events)
{
    public static DbStore Connect(string endpoint, string key)
    {
        var client = new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        });
        var db = client.GetDatabase("orc");
        return new DbStore(db.GetContainer("config"), db.GetContainer("commands"), db.GetContainer("state"), db.GetContainer("events"));
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

    public Task SaveConfigAsync(ConfigDoc doc) => Retry.WriteAsync(() => config.UpsertItemAsync(doc, new PartitionKey(doc.Id)));

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

    public async Task<NudgeDoc> LoadNudgesAsync()
    {
        try
        {
            return (await state.ReadItemAsync<NudgeDoc>("nudges", new PartitionKey("nudges"))).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new NudgeDoc();
        }
    }

    public Task SaveNudgesAsync(NudgeDoc doc) => state.UpsertItemAsync(doc, new PartitionKey(doc.Id));

    public async Task<string?> LoadFeedTokenAsync()
    {
        try
        {
            return (await state.ReadItemAsync<FeedDoc>("feed", new PartitionKey("feed"))).Resource.Token;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task SaveFeedTokenAsync(string? token) =>
        Retry.WriteAsync(() => state.UpsertItemAsync(new FeedDoc { Token = token }, new PartitionKey("feed")));

    /// <summary>Reads what the webhook recorded since <paramref name="token"/>. With no token, starts from now.</summary>
    public async Task<(List<EventDoc> Events, string? Token)> ReadEventsAsync(string? token)
    {
        var start = token is null ? ChangeFeedStartFrom.Now() : ChangeFeedStartFrom.ContinuationToken(token);
        using var feed = events.GetChangeFeedIterator<EventDoc>(start, ChangeFeedMode.LatestVersion);
        var seen = new List<EventDoc>();
        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync();
            token = page.ContinuationToken;
            if (page.StatusCode == HttpStatusCode.NotModified) break;
            seen.AddRange(page);
        }
        return (seen, token);
    }

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

/// <summary>Retries an idempotent write after a transient Cosmos failure. The SDK does not retry a write that timed out.</summary>
public static class Retry
{
    public static async Task WriteAsync(Func<Task> write, int attempts = 3, int delayMs = 1000)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await write();
                return;
            }
            catch (CosmosException e) when (attempt < attempts && e.StatusCode is (HttpStatusCode.RequestTimeout or HttpStatusCode.ServiceUnavailable))
            {
                await Task.Delay(delayMs * attempt);
            }
        }
    }
}
