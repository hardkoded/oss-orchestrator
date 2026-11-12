using OssOrchestrator.Agent;

// Source of truth: the Cosmos database (orc/config, orc/commands, orc/state). Each pass reads config and
// pending commands, looks at Herdr sessions and GitHub, plans, launches, and writes the state back.
// Dry run is the default: it plans and publishes, but does not start sessions. Set ORC_DRY_RUN=false to launch.
var dryRun = Environment.GetEnvironmentVariable("ORC_DRY_RUN") != "false";
var endpoint = Environment.GetEnvironmentVariable("ORC_COSMOS_ENDPOINT")
    ?? throw new InvalidOperationException("Set ORC_COSMOS_ENDPOINT.");
var key = Environment.GetEnvironmentVariable("ORC_COSMOS_KEY")
    ?? throw new InvalidOperationException("Set ORC_COSMOS_KEY (kept on this machine only).");
var panelRepos = Environment.GetEnvironmentVariable("ORC_PANEL_REPOS")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "herdr-app", "orc-data", "repos.json");
var intervalSec = int.TryParse(Environment.GetEnvironmentVariable("ORC_INTERVAL_SEC"), out var s) ? s : 120;

var log = Console.Out;
var db = DbStore.Connect(endpoint, key);
var github = new GitHub();
var herdr = new Herdr(dryRun, log);

await log.WriteLineAsync($"orc agent started (dryRun={dryRun}, every {intervalSec}s, database = source of truth)");

while (true)
{
    try
    {
        var config = await db.LoadConfigAsync(panelRepos);

        foreach (var cmd in await db.PendingCommandsAsync())
        {
            var result = Applier.Apply(config, cmd);
            await log.WriteLineAsync($"command {cmd.Type}: {result}");
            await db.SaveConfigAsync(config);
            cmd.Processed = true;
            cmd.Result = result;
            await db.MarkCommandAsync(cmd);
        }

        var issues = new Dictionary<string, IReadOnlyList<IssueInfo>>();
        var prs = new Dictionary<string, IReadOnlyList<PrInfo>>();
        var finished = new Dictionary<string, IReadOnlySet<int>>();
        var inProgress = new Dictionary<string, IReadOnlySet<int>>();
        var errors = new Dictionary<string, string>();
        foreach (var repo in config.Repos)
        {
            var settings = new RepoSettings(repo.Repo, repo.Folder, repo.Paused, repo.Auto);
            try { issues[repo.Repo] = await github.OpenIssuesAsync(settings, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { prs[repo.Repo] = await github.OpenPrsAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { finished[repo.Repo] = await github.FinishedPrIssuesAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { inProgress[repo.Repo] = await github.InProgressIssuesAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
        }

        var sessions = await herdr.ListSessionsAsync(CancellationToken.None);
        var running = sessions.Count;

        // Free the in-progress label when the issue's PR is merged or closed. Only when this pass read both the open
        // and the finished PRs of the repo, so a failed lookup never clears a label by mistake, and never while a session
        // is still on the issue.
        foreach (var repo in config.Repos)
        {
            if (!inProgress.TryGetValue(repo.Repo, out var labelled) || !prs.TryGetValue(repo.Repo, out var openPrs)
                || !finished.TryGetValue(repo.Repo, out var doneIssues)) continue;
            var openPrIssues = openPrs.Where(p => p.Issue is not null).Select(p => p.Issue!.Value).ToHashSet();
            foreach (var number in labelled)
            {
                if (openPrIssues.Contains(number) || !doneIssues.Contains(number)) continue;
                if (sessions.Any(s => s.Repo == repo.Repo && s.Issue == number)) continue;
                if (dryRun)
                {
                    await log.WriteLineAsync($"{DateTime.Now:T} dry run: would remove in-progress from {repo.Repo}#{number}");
                    continue;
                }
                try
                {
                    await IssueLabels.RemoveInProgressAsync(repo.Folder, number, CancellationToken.None);
                    await log.WriteLineAsync($"{DateTime.Now:T} removed in-progress from {repo.Repo}#{number} (PR finished)");
                }
                catch (Exception e)
                {
                    await log.WriteLineAsync($"{DateTime.Now:T} could not remove in-progress from {repo.Repo}#{number}: {e.Message}");
                }
            }
        }

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
            var repo = config.Repos.First(r => r.Repo == launch.Repo);
            var task = Brief.For(repo.Repo, launch.Issue);
            await herdr.LaunchAsync(repo.Repo, repo.Folder, launch.Issue, task, CancellationToken.None);
            if (!dryRun)
            {
                // Record the launch first: the session is already running, so the issue must not be planned again
                // even if the label step fails. The label is then best effort and logged when it fails.
                repo.Launched[launch.Issue.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await db.SaveConfigAsync(config);
                try
                {
                    await IssueLabels.MarkInProgressAsync(repo.Folder, launch.Issue, CancellationToken.None);
                }
                catch (Exception e)
                {
                    await log.WriteLineAsync($"{DateTime.Now:T} launched #{launch.Issue} but could not label it: {e.Message}");
                }
            }
        }

        await db.PutStateAsync(new StateDoc
        {
            At = DateTimeOffset.UtcNow,
            DryRun = dryRun,
            Running = running,
            MaxTotal = config.MaxTotal,
            Planned = plan.Select(p => $"{p.Repo}#{p.Issue}").ToList(),
            Repos = config.Repos.Select(r => new RepoStatus
            {
                Repo = r.Repo,
                Paused = r.Paused,
                Auto = r.Auto,
                OpenIssues = issues.TryGetValue(r.Repo, out var list) ? list.Count : 0,
                Error = errors.GetValueOrDefault(r.Repo),
            }).ToList(),
            Sessions = sessions.Select(sess =>
            {
                var pr = prs.GetValueOrDefault(sess.Repo)?.FirstOrDefault(p => p.Issue == sess.Issue);
                var title = issues.GetValueOrDefault(sess.Repo)?.FirstOrDefault(i => i.Number == sess.Issue)?.Title ?? "";
                return new SessionStatus
                {
                    Repo = sess.Repo,
                    Issue = sess.Issue,
                    Title = title,
                    Status = sess.Status,
                    PrNumber = pr?.Number,
                    PrUrl = pr?.Url,
                    PrConflict = pr?.Mergeable == "CONFLICTING",
                    PrDraft = pr?.IsDraft ?? false,
                    PrStatus = pr?.Status ?? "pending",
                };
            }).ToList(),
            Issues = config.Repos.Select(r => new RepoIssues
            {
                Repo = r.Repo,
                Items = (issues.GetValueOrDefault(r.Repo) ?? []).Select(i => new IssueStatus
                {
                    Number = i.Number,
                    Title = i.Title,
                    Assigned = i.Assigned,
                    InProgress = i.InProgress,
                    Blocked = i.Blocked,
                    Ignored = r.Ignored.Contains(i.Number),
                }).ToList(),
            }).ToList(),
        });
        await log.WriteLineAsync($"{DateTime.Now:T} pass done: running={running} planned={plan.Count} errors={errors.Count}");
    }
    catch (Exception e)
    {
        await log.WriteLineAsync($"{DateTime.Now:T} pass failed: {e.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(intervalSec));
}
