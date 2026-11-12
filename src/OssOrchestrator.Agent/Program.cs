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
            var result = cmd.Type switch
            {
                "unblock" => await UnblockAsync(config, cmd, dryRun, log),
                "block_session" => await BlockSessionAsync(config, cmd, dryRun, herdr, log),
                _ => Applier.Apply(config, cmd),
            };
            await log.WriteLineAsync($"command {cmd.Type}: {result}");
            await db.SaveConfigAsync(config);
            cmd.Processed = true;
            cmd.Result = result;
            await db.MarkCommandAsync(cmd);
        }

        var issues = new Dictionary<string, IReadOnlyList<IssueInfo>>();
        var prs = new Dictionary<string, IReadOnlyList<PrInfo>>();
        var finished = new Dictionary<string, IReadOnlyDictionary<int, PrInfo>>();
        var inProgress = new Dictionary<string, IReadOnlySet<int>>();
        var errors = new Dictionary<string, string>();
        foreach (var repo in config.Repos)
        {
            var settings = new RepoSettings(repo.Repo, repo.Folder, repo.Paused, repo.Auto);
            try { issues[repo.Repo] = await github.OpenIssuesAsync(settings, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { prs[repo.Repo] = await github.OpenPrsAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { finished[repo.Repo] = await github.FinishedPrsByIssueAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { inProgress[repo.Repo] = await github.InProgressIssuesAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
        }

        var sessions = await herdr.ListSessionsAsync(CancellationToken.None);
        // A session whose issue is closed takes no slot and is not shown. Only sessions missing from the open-issue list are checked on GitHub.
        var shown = new List<SessionInfo>();
        foreach (var sess in sessions)
        {
            var listed = issues.GetValueOrDefault(sess.Repo)?.Any(i => i.Number == sess.Issue) ?? false;
            var folder = config.Repos.FirstOrDefault(r => r.Repo == sess.Repo)?.Folder;
            var open = true;
            if (!listed && folder is not null)
            {
                try { open = await github.IssueIsOpenAsync(folder, sess.Issue, CancellationToken.None); }
                catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not read state of {sess.Repo}#{sess.Issue}: {e.Message}"); }
            }
            if (!open) continue;
            var listedSession = sess;
            // Herdr says "idle" or "done" for a worker waiting on a question too, so the screen is checked.
            if (sess.Terminal is not null && sess.Status is "done" or "idle")
            {
                try
                {
                    if (await ScreenReader.NeedsAnswerAsync(sess.Terminal, CancellationToken.None))
                        listedSession = sess with { Status = "blocked" };
                }
                catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not read the screen of {sess.Repo}#{sess.Issue}: {e.Message}"); }
            }
            shown.Add(listedSession);
        }
        // Cloud sessions stay listed while their issue is open and has no finished PR. They take a slot like a local one.
        var cloudChanged = false;
        foreach (var repo in config.Repos)
        {
            if (!issues.TryGetValue(repo.Repo, out var repoIssues)) continue;
            var listed = repoIssues.Select(i => i.Number).ToHashSet();
            var done = finished.GetValueOrDefault(repo.Repo);
            foreach (var (issueKey, doc) in repo.Cloud.ToList())
            {
                var number = int.Parse(issueKey);
                var open = listed.Contains(number);
                if (!open)
                {
                    try { open = await github.IssueIsOpenAsync(repo.Folder, number, CancellationToken.None); }
                    catch (Exception) { open = true; }   // when GitHub cannot say, keep the session listed
                }
                if (!open || (done?.ContainsKey(number) ?? false))
                {
                    repo.Cloud.Remove(issueKey);
                    cloudChanged = true;
                    continue;
                }
                shown.Add(new SessionInfo(repo.Repo, number, "cloud", null, doc.Id, doc.Url));
            }
        }
        if (cloudChanged) await db.SaveConfigAsync(config);

        var running = shown.Count;

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
                if (openPrIssues.Contains(number) || !doneIssues.ContainsKey(number)) continue;
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
            if (!dryRun)
            {
                try
                {
                    await Brief.SyncReferenceAsync(repo.Repo, CancellationToken.None);
                }
                catch (Exception e)
                {
                    await log.WriteLineAsync($"{DateTime.Now:T} could not sync the reference fork for {repo.Repo}: {e.Message}");
                }
            }
            if (config.Mode == "cloud")
            {
                if (dryRun)
                {
                    await log.WriteLineAsync($"{DateTime.Now:T} [dry-run] would launch {repo.Repo}#{launch.Issue} in the cloud");
                }
                else
                {
                    try
                    {
                        var cloud = await CloudSessions.LaunchAsync(repo.Repo, repo.Folder, launch.Issue, task, CancellationToken.None);
                        repo.Cloud[launch.Issue.ToString()] = cloud;
                        await log.WriteLineAsync($"{DateTime.Now:T} started cloud session for {repo.Repo}#{launch.Issue}: {cloud.Url}");
                    }
                    catch (Exception e)
                    {
                        await log.WriteLineAsync($"{DateTime.Now:T} could not start a cloud session for {repo.Repo}#{launch.Issue}: {e.Message}");
                        continue;
                    }
                }
            }
            else
            {
                await herdr.LaunchAsync(repo.Repo, repo.Folder, launch.Issue, task, CancellationToken.None);
            }
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
            // The dashboard counts the sessions it lists. The planner still counts every open tab against the limit.
            Running = shown.Count,
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
            Sessions = shown.Select(sess =>
            {
                // The open PR for the issue, or else its newest merged or closed PR, so finished work still shows its PR.
                var pr = prs.GetValueOrDefault(sess.Repo)?.FirstOrDefault(p => p.Issue == sess.Issue)
                    ?? finished.GetValueOrDefault(sess.Repo)?.GetValueOrDefault(sess.Issue);
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
                    CloudSessionId = sess.CloudSessionId,
                    CloudSessionUrl = sess.CloudSessionUrl,
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

    await WaitForNextPassAsync(db, intervalSec, log);
}

// Blocks an issue and releases its session. The blocked label is set first: if that fails, the session keeps running.
static async Task<string> BlockSessionAsync(ConfigDoc config, CommandDoc cmd, bool dryRun, Herdr herdr, TextWriter log)
{
    var repo = config.Repos.FirstOrDefault(r => r.Repo == cmd.Repo);
    if (repo is null) return $"unknown repo {cmd.Repo}";
    if (cmd.Issue is not int n) return "missing issue number";
    if (dryRun) return $"dry run: would block {repo.Repo} #{n} and close its session";
    try
    {
        await IssueLabels.MarkBlockedAsync(repo.Folder, n, CancellationToken.None);
    }
    catch (Exception e)
    {
        await log.WriteLineAsync($"{DateTime.Now:T} could not block {repo.Repo}#{n}: {e.Message}");
        return $"could not block {repo.Repo} #{n}: {e.Message}";
    }
    try
    {
        repo.Cloud.Remove(n.ToString());   // a cloud session is no longer tracked; it keeps running in claude.ai
        await herdr.CloseSessionAsync(repo.Repo, n, CancellationToken.None);
        // The session is released, so the issue is no longer in progress.
        try { await IssueLabels.RemoveInProgressAsync(repo.Folder, n, CancellationToken.None); }
        catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not clear in-progress on {repo.Repo}#{n}: {e.Message}"); }
        return $"blocked {repo.Repo} #{n} and closed its session";
    }
    catch (Exception e)
    {
        await log.WriteLineAsync($"{DateTime.Now:T} blocked {repo.Repo}#{n} but could not close its session: {e.Message}");
        return $"blocked {repo.Repo} #{n}, but its session did not close: {e.Message}";
    }
}

// Waits between passes, but wakes early when a dashboard Refresh is queued, so the next pass starts right away.
static async Task WaitForNextPassAsync(DbStore db, int seconds, TextWriter log)
{
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            if ((await db.PendingCommandsAsync()).Any(c => c.Type == "refresh"))
            {
                await log.WriteLineAsync($"{DateTime.Now:T} refresh requested: running a pass now");
                return;
            }
        }
        catch (Exception e)
        {
            await log.WriteLineAsync($"{DateTime.Now:T} refresh check failed: {e.Message}");
        }
    }
}

// Removes the blocked label on GitHub. Failures become the command's result, so one bad command never stops the pass.
static async Task<string> UnblockAsync(ConfigDoc config, CommandDoc cmd, bool dryRun, TextWriter log)
{
    var repo = config.Repos.FirstOrDefault(r => r.Repo == cmd.Repo);
    if (repo is null) return $"unknown repo {cmd.Repo}";
    if (cmd.Issue is not int n) return "missing issue number";
    if (dryRun) return $"dry run: would unblock {repo.Repo} #{n}";
    try
    {
        await IssueLabels.RemoveBlockedAsync(repo.Folder, n, CancellationToken.None);
        return $"unblocked {repo.Repo} #{n}";
    }
    catch (Exception e)
    {
        await log.WriteLineAsync($"{DateTime.Now:T} could not unblock {repo.Repo}#{n}: {e.Message}");
        return $"could not unblock {repo.Repo} #{n}: {e.Message}";
    }
}
