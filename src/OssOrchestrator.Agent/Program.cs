using OssOrchestrator.Agent;

// Source of truth: the Cosmos database (orc/config, orc/commands, orc/state). Each pass reads config and
// pending commands, looks at the cloud sessions and GitHub, plans, launches, and writes the state back.
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
var nudgeEvery = TimeSpan.FromMinutes(30);

await log.WriteLineAsync($"orc agent started (dryRun={dryRun}, every {intervalSec}s, database = source of truth)");

while (true)
{
    try
    {
        var config = await db.LoadConfigAsync(panelRepos);
        GhEnv.Register(config);
        // "repo#issue" -> the PR state a worker was last told about, so a red or conflicted PR is not repeated every pass.
        // It lives in the database, so it survives an agent restart.
        var nudgeDoc = await db.LoadNudgesAsync();
        var nudged = nudgeDoc.Items;
        var nudgedBefore = System.Text.Json.JsonSerializer.Serialize(nudged);

        foreach (var cmd in await db.PendingCommandsAsync())
        {
            var result = cmd.Type switch
            {
                "unblock" => await UnblockAsync(config, cmd, dryRun, log),
                "block_session" => await BlockSessionAsync(config, cmd, dryRun, log),
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
        var needsAnswer = new Dictionary<string, IReadOnlySet<int>>();
        var errors = new Dictionary<string, string>();
        var closedLast24h = 0;
        foreach (var repo in config.Repos)
        {
            // Not critical: a failed lookup only leaves this repo out of the closed-in-24h count.
            try { closedLast24h += await github.ClosedSinceAsync(repo.Folder, DateTimeOffset.UtcNow.AddHours(-24), CancellationToken.None); }
            catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not read closed issues of {repo.Repo}: {e.Message}"); }
            var settings = new RepoSettings(repo.Repo, repo.Folder, repo.Paused, repo.Auto);
            try { issues[repo.Repo] = await github.OpenIssuesAsync(settings, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { prs[repo.Repo] = await github.OpenPrsAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { finished[repo.Repo] = await github.FinishedPrsByIssueAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { inProgress[repo.Repo] = await github.InProgressIssuesAsync(repo.Folder, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
            try { needsAnswer[repo.Repo] = await github.IssuesWithLabelAsync(repo.Folder, IssueLabels.NeedsAnswer, CancellationToken.None); }
            catch (Exception e) { errors[repo.Repo] = e.Message; }
        }

        var shown = new List<SessionInfo>();
        // Cloud sessions stay listed while their issue is open and has no finished PR. They take a slot each.
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
                // A cloud worker has no terminal to read, so it reports a question through the needs-answer label.
                var waiting = needsAnswer.GetValueOrDefault(repo.Repo)?.Contains(number) ?? false;
                shown.Add(new SessionInfo(repo.Repo, number, waiting ? "blocked" : "cloud", doc.Id, doc.Url));
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
                if (shown.Any(s => s.Repo == repo.Repo && s.Issue == number)) continue;
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

        var unsaved = false;
        foreach (var launch in plan)
        {
            var repo = config.Repos.First(r => r.Repo == launch.Repo);
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
            if (dryRun)
            {
                await log.WriteLineAsync($"{DateTime.Now:T} [dry-run] would launch {repo.Repo}#{launch.Issue} in the cloud");
            }
            else
            {
                try
                {
                    var cloud = await CloudSessions.LaunchRoutineAsync(repo.Repo, launch.Issue, CancellationToken.None);
                    repo.Cloud[launch.Issue.ToString()] = cloud;
                    await log.WriteLineAsync($"{DateTime.Now:T} started cloud session for {repo.Repo}#{launch.Issue}: {cloud.Url}");
                }
                catch (Exception e)
                {
                    await log.WriteLineAsync($"{DateTime.Now:T} could not start a cloud session for {repo.Repo}#{launch.Issue}: {e.Message}");
                    continue;
                }
            }
            if (!dryRun)
            {
                // The session is already running, so the issue must not be planned again. The in-progress label is what
                // stops that, so a failed save never skips the label. It is tried again at the end of the pass.
                repo.Launched[launch.Issue.ToString()] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try { await db.SaveConfigAsync(config); unsaved = false; }
                catch (Exception e)
                {
                    unsaved = true;
                    await log.WriteLineAsync($"{DateTime.Now:T} launched {repo.Repo}#{launch.Issue} ({repo.Cloud.GetValueOrDefault(launch.Issue.ToString())?.Url}) but could not save it: {e.Message}");
                }
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

        // A worker on a PR that is red or conflicted is told so, by a message to its cloud session.
        foreach (var sess in shown)
        {
            var nudgeKey = $"{sess.Repo}#{sess.Issue}";
            var pr = prs.GetValueOrDefault(sess.Repo)?.FirstOrDefault(p => p.Issue == sess.Issue);
            if (pr is null || pr.Status is not ("failed" or "conflicted"))
            {
                nudged.Remove(nudgeKey);
                continue;
            }
            if (nudged.TryGetValue(nudgeKey, out var last) && last.Pr == pr.Number && last.Status == pr.Status
                && DateTimeOffset.UtcNow - last.At < nudgeEvery) continue;
            var text = pr.Status == "conflicted"
                ? $"PR #{pr.Number} has merge conflicts. Resolve them and push."
                : $"The build is red on PR #{pr.Number}. Fix the failing checks and push.";
            try
            {
                var sent = dryRun
                    ? await DryRunLogAsync(log, nudgeKey, text)
                    : await CloudSessions.SendAsync(sess.CloudSessionId, text, CancellationToken.None);
                if (sent)
                {
                    nudged[nudgeKey] = new NudgeEntry { Pr = pr.Number, Status = pr.Status, At = DateTimeOffset.UtcNow };
                    await log.WriteLineAsync($"{DateTime.Now:T} told {nudgeKey} its PR is {pr.Status}");
                }
            }
            catch (CloudSessionGoneException)
            {
                // The session is gone, so stop tracking it. This frees its slot and ends the repeated error.
                config.Repos.FirstOrDefault(r => r.Repo == sess.Repo)?.Cloud.Remove(sess.Issue.ToString());
                nudged.Remove(nudgeKey);
                unsaved = true;
                await log.WriteLineAsync($"{DateTime.Now:T} {nudgeKey}: cloud session {sess.CloudSessionId} no longer exists, stopped tracking it");
            }
            catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not message {nudgeKey}: {e.Message}"); }
        }
        if (unsaved)
        {
            try { await db.SaveConfigAsync(config); }
            catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not save the config: {e.Message}"); }
        }
        if (System.Text.Json.JsonSerializer.Serialize(nudged) != nudgedBefore)
        {
            try { await db.SaveNudgesAsync(nudgeDoc); }
            catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not save the nudge memory: {e.Message}"); }
        }

        await db.PutStateAsync(new StateDoc
        {
            At = DateTimeOffset.UtcNow,
            DryRun = dryRun,
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
                    StartedAt = StartedAt(config, sess),
                };
            }).ToList(),
            ClosedLast24h = closedLast24h,
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
static async Task<string> BlockSessionAsync(ConfigDoc config, CommandDoc cmd, bool dryRun, TextWriter log)
{
    var repo = config.Repos.FirstOrDefault(r => r.Repo == cmd.Repo);
    if (repo is null) return $"unknown repo {cmd.Repo}";
    if (cmd.Issue is not int n) return "missing issue number";
    if (dryRun) return $"dry run: would block {repo.Repo} #{n} and stop tracking its session";
    try
    {
        await IssueLabels.MarkBlockedAsync(repo.Folder, n, CancellationToken.None);
    }
    catch (Exception e)
    {
        await log.WriteLineAsync($"{DateTime.Now:T} could not block {repo.Repo}#{n}: {e.Message}");
        return $"could not block {repo.Repo} #{n}: {e.Message}";
    }
    repo.Cloud.Remove(n.ToString());   // a cloud session is no longer tracked; it keeps running in claude.ai
    // The session is released, so the issue is no longer in progress.
    try { await IssueLabels.RemoveInProgressAsync(repo.Folder, n, CancellationToken.None); }
    catch (Exception e) { await log.WriteLineAsync($"{DateTime.Now:T} could not clear in-progress on {repo.Repo}#{n}: {e.Message}"); }
    return $"blocked {repo.Repo} #{n} and stopped tracking its session";
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

// The launch time the agent recorded: the cloud document's creation time, else the launch stamp of the issue.
static DateTimeOffset? StartedAt(ConfigDoc config, SessionInfo sess)
{
    var repo = config.Repos.FirstOrDefault(r => r.Repo == sess.Repo);
    if (repo is null) return null;
    var key = sess.Issue.ToString();
    if (repo.Cloud.TryGetValue(key, out var cloud) && cloud.CreatedAt != default) return cloud.CreatedAt;
    return repo.Launched.TryGetValue(key, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}

static async Task<bool> DryRunLogAsync(TextWriter log, string key, string text)
{
    await log.WriteLineAsync($"[dry-run] would send to {key}: {text}");
    return true;
}
