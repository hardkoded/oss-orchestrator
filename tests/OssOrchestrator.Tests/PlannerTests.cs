using OssOrchestrator.Agent;

namespace OssOrchestrator.Tests;

public class PlannerTests
{
    private static readonly RepoSettings Auto = new("o/r", @"C:\r", Paused: false, Auto: true);

    private static PlanInput Input(int running = 0, RepoSettings[]? repos = null, IssueInfo[]? issues = null,
        string[]? ignored = null, Dictionary<string, long>? launched = null) => new(
            MaxTotal: 6, Running: running,
            Repos: repos ?? [Auto],
            IssuesByRepo: new Dictionary<string, IReadOnlyList<IssueInfo>> { ["o/r"] = issues ?? [new(1, "a", false), new(2, "b", false)] },
            Ignored: new HashSet<string>(ignored ?? []),
            LaunchedAt: launched ?? new Dictionary<string, long>(),
            NowMs: 1_000_000_000);

    [Fact]
    public void Launches_lowest_numbers_up_to_the_free_slots()
    {
        var result = Planner.Plan(Input(running: 5));
        Assert.Equal([1], result.Select(r => r.Issue));
    }

    [Fact]
    public void Global_limit_is_respected_when_full()
    {
        Assert.Empty(Planner.Plan(Input(running: 6)));
    }

    [Fact]
    public void Paused_or_non_auto_repos_are_skipped()
    {
        Assert.Empty(Planner.Plan(Input(repos: [Auto with { Paused = true }])));
        Assert.Empty(Planner.Plan(Input(repos: [Auto with { Auto = false }])));
    }

    [Fact]
    public void Assigned_ignored_and_recently_launched_issues_are_skipped()
    {
        var issues = new[] { new IssueInfo(1, "a", true), new IssueInfo(2, "b", false), new IssueInfo(3, "c", false) };
        var result = Planner.Plan(Input(issues: issues, ignored: ["o/r#2"], launched: new() { ["o/r#3"] = 1_000_000_000 - 60_000 }));
        Assert.Empty(result);
    }

    [Fact]
    public void Issues_labelled_in_progress_or_blocked_are_skipped()
    {
        var issues = new[]
        {
            new IssueInfo(1, "a", false, InProgress: true),
            new IssueInfo(2, "b", false, Blocked: true),
            new IssueInfo(3, "c", false),
        };
        var result = Planner.Plan(Input(issues: issues));
        Assert.Equal([3], result.Select(l => l.Issue));
    }

    [Fact]
    public void A_launch_older_than_15_minutes_is_eligible_again()
    {
        var result = Planner.Plan(Input(issues: [new(3, "c", false)], launched: new() { ["o/r#3"] = 1_000_000_000 - 16 * 60_000 }));
        Assert.Equal([3], result.Select(r => r.Issue));
    }
}
