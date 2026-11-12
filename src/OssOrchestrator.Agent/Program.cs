using System.Text.Json;
using Azure.Storage.Queues;
using OssOrchestrator.Agent;

// Dry run is the default: the agent plans and publishes, but does not start Claude sessions.
// Set ORC_DRY_RUN=false only after the panel's own auto-launch is turned off.
var dryRun = Environment.GetEnvironmentVariable("ORC_DRY_RUN") != "false";
var dataDir = Environment.GetEnvironmentVariable("ORC_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "herdr-app", "orc-data");
var intervalSec = int.TryParse(Environment.GetEnvironmentVariable("ORC_INTERVAL_SEC"), out var s) ? s : 60;
var connection = Environment.GetEnvironmentVariable("ORC_QUEUE_CONNECTION")
    ?? throw new InvalidOperationException("Set ORC_QUEUE_CONNECTION to the Storage Queue connection string.");

var log = Console.Out;
var config = new AgentConfig { DataDir = dataDir };
var github = new GitHub();
var herdr = new Herdr(dryRun, log);
var commands = new QueueClient(connection, "orc-commands");
var state = new QueueClient(connection, "orc-state");
await commands.CreateIfNotExistsAsync();
await state.CreateIfNotExistsAsync();

await log.WriteLineAsync($"orc agent started (dryRun={dryRun}, data={dataDir}, every {intervalSec}s)");

while (true)
{
    try
    {
        config.Load();
        await ApplyCommandsAsync(config, commands, log);

        var issues = new Dictionary<string, IReadOnlyList<IssueInfo>>();
        var errors = new Dictionary<string, string>();
        foreach (var repo in config.Repos)
        {
            try { issues[repo.Repo] = await github.OpenIssuesAsync(new RepoSettings(repo.Repo, repo.Folder, repo.Paused, repo.Auto), CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
        }

        var running = config.RunningNow();
        var plan = Planner.Plan(new PlanInput(
            MaxTotal: config.MaxTotal,
            Running: running,
            Repos: config.Repos.Select(r => new RepoSettings(r.Repo, r.Folder, r.Paused, r.Auto)).ToList(),
            IssuesByRepo: issues,
            Ignored: config.Repos.SelectMany(r => r.Ignored.Select(n => $"{r.Repo}#{n}")).ToHashSet(),
            LaunchedAt: config.Repos.SelectMany(r => r.Launched.Select(kv => ($"{r.Repo}#{kv.Key}", kv.Value)))
                .ToDictionary(x => x.Item1, x => x.Value),
            NowMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        foreach (var launch in plan)
        {
            var repo = config.Find(launch.Repo)!;
            await herdr.LaunchAsync(new RepoSettings(repo.Repo, repo.Folder, repo.Paused, repo.Auto), launch.Issue,
                $"You are a worker of the issue orchestrator. Resolve issue #{launch.Issue} of {repo.Repo}.", CancellationToken.None);
        }

        await PublishAsync(state, config, running, issues, errors, plan.Count, dryRun);
        await log.WriteLineAsync($"{DateTime.Now:T} pass done: running={running} planned={plan.Count} errors={errors.Count}");
    }
    catch (Exception e)
    {
        await log.WriteLineAsync($"{DateTime.Now:T} pass failed: {e.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(intervalSec));
}

static async Task ApplyCommandsAsync(AgentConfig config, QueueClient queue, TextWriter log)
{
    var batch = await queue.ReceiveMessagesAsync(maxMessages: 10, visibilityTimeout: TimeSpan.FromMinutes(1));
    foreach (var message in batch.Value)
    {
        try
        {
            var cmd = JsonSerializer.Deserialize<DashboardCommand>(message.MessageText,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cmd is not null) await log.WriteLineAsync($"command {cmd.Type}: {CommandApplier.Apply(config, cmd)}");
        }
        catch (JsonException e)
        {
            await log.WriteLineAsync($"bad command message dropped: {e.Message}");
        }
        await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
    }
}

static async Task PublishAsync(QueueClient state, AgentConfig config, int running,
    Dictionary<string, IReadOnlyList<IssueInfo>> issues, Dictionary<string, string> errors, int planned, bool dryRun)
{
    var snapshot = new
    {
        at = DateTimeOffset.UtcNow,
        dryRun,
        running,
        maxTotal = config.MaxTotal,
        planned,
        repos = config.Repos.Select(r => new
        {
            repo = r.Repo,
            paused = r.Paused,
            auto = r.Auto,
            openIssues = issues.TryGetValue(r.Repo, out var list) ? list.Count : 0,
            error = errors.GetValueOrDefault(r.Repo),
        }),
    };
    // Only the latest snapshot matters: clear the queue, then send one message that expires in 10 minutes.
    await state.ClearMessagesAsync();
    await state.SendMessageAsync(JsonSerializer.Serialize(snapshot), timeToLive: TimeSpan.FromMinutes(10));
}
