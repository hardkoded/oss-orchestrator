using OssOrchestrator.Web;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Tests;

public class SessionGroupsTests
{
    private static SessionStatus Session(int issue, string status = "cloud", int? blockedBy = null, int? pr = null, string prStatus = "pending", string repo = "o/a") =>
        new() { Repo = repo, Issue = issue, Status = status, Blockers = blockedBy is int b ? [b] : [], PrNumber = pr, PrStatus = prStatus };

    private static SessionStatus Waiting(int issue, params int[] blockers) =>
        new() { Repo = "o/a", Issue = issue, Status = "waiting", Blockers = [.. blockers] };

    private static SessionBoard Build(params SessionStatus[] sessions) => SessionGroups.Build(sessions, []);

    [Theory]
    [InlineData("blocked", "conflicted", false, SessionGroup.NeedsAnswer)]
    [InlineData("waiting", "ready", false, SessionGroup.Waiting)]
    [InlineData("cloud", "failed", false, SessionGroup.Fixing)]
    [InlineData("cloud", "conflicted", true, SessionGroup.Fixing)]
    [InlineData("cloud", "ready", false, SessionGroup.ReadyToMerge)]
    [InlineData("cloud", "ready", true, SessionGroup.Working)]
    [InlineData("cloud", "building", false, SessionGroup.Checks)]
    [InlineData("cloud", "building", true, SessionGroup.Working)]
    [InlineData("cloud", "pending", false, SessionGroup.Working)]
    public void The_first_matching_rule_gives_the_group(string status, string prStatus, bool draft, SessionGroup expected)
    {
        var session = Session(1, status, prStatus: prStatus);
        session.PrDraft = draft;
        Assert.Equal(expected, SessionGroups.Of(session));
    }

    [Theory]
    [InlineData(11, null, true)]
    [InlineData(9, null, false)]
    [InlineData(11, 5, false)]
    public void A_session_without_a_PR_after_ten_hours_may_be_stuck(int hours, int? pr, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var session = Session(1, pr: pr);
        session.StartedAt = now.AddHours(-hours);
        Assert.Equal(expected, SessionGroups.MayBeStuck(session, now));
    }

    [Fact]
    public void A_chain_of_waiting_sessions_hangs_under_its_blocker()
    {
        var board = Build(Session(1, "blocked"), Session(2, "waiting", blockedBy: 1), Session(3, "waiting", blockedBy: 2));

        var root = Assert.Single(board.In(SessionGroup.NeedsAnswer));
        Assert.Equal(2, root.Blocks);
        Assert.Equal([1, 2, 3], root.Tree.Select(n => n.Session.Issue));
        Assert.Empty(board.In(SessionGroup.Waiting));
        Assert.Empty(board.Outside);
    }

    [Fact]
    public void A_session_with_several_blockers_is_listed_under_the_first_and_counted_for_each()
    {
        var board = Build(Session(1, "blocked"), Session(2, "blocked"), Waiting(3, 1, 2, 9), Waiting(4, 3, 2));

        var first = board.All.Single(n => n.Session.Issue == 1);
        var second = board.All.Single(n => n.Session.Issue == 2);
        Assert.Equal([1, 3, 4], first.Tree.Select(n => n.Session.Issue));
        Assert.Equal([2], second.Tree.Select(n => n.Session.Issue));
        Assert.Equal([3, 4], first.Held.Select(n => n.Session.Issue));
        Assert.Equal([3, 4], second.Held.Select(n => n.Session.Issue).Order());
        Assert.Equal([1, 2, null], board.All.Single(n => n.Session.Issue == 3).On.Select(b => b.Node?.Session.Issue));
    }

    [Fact]
    public void A_ring_through_a_later_blocker_is_marked()
    {
        var board = Build(Session(1, "blocked"), Waiting(2, 1, 3), Waiting(3, 2));

        Assert.Equal([1, 2, 3], Assert.Single(board.Roots).Tree.Select(n => n.Session.Issue));
        Assert.Equal([false, true, true], board.All.Select(n => n.InCycle));
        Assert.Equal([3], board.All.Single(n => n.Session.Issue == 2).Held.Select(n => n.Session.Issue));
    }

    [Fact]
    public void A_session_whose_first_blocker_has_no_session_is_listed_under_it()
    {
        var board = Build(Session(1, "blocked"), Waiting(2, 9, 1));

        Assert.Equal(9, Assert.Single(board.Outside).Number);
        Assert.Equal(1, Assert.Single(board.In(SessionGroup.NeedsAnswer)).Blocks);
    }

    [Fact]
    public void A_blocker_can_be_named_by_its_PR_number_but_only_in_the_same_repo()
    {
        var board = Build(Session(1, pr: 40, repo: "o/b"), Session(2, pr: 40), Session(3, "waiting", blockedBy: 40));

        Assert.Equal(3, Assert.Single(board.All.Single(n => n.Session.Issue == 2).Waiting).Session.Issue);
        Assert.Single(Build(Session(2, pr: 40), Waiting(3, 2, 40)).All[1].On);
        Assert.Empty(board.All.Single(n => n.Session.Issue == 1).Waiting);
    }

    [Fact]
    public void Questions_that_block_the_most_come_first_then_the_oldest()
    {
        var older = Session(1, "blocked");
        older.StartedAt = DateTimeOffset.UtcNow.AddHours(-5);
        var newer = Session(2, "blocked");
        newer.StartedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var blocking = Session(3, "blocked");
        blocking.StartedAt = DateTimeOffset.UtcNow;
        var board = Build(newer, older, blocking, Session(4, "waiting", blockedBy: 3));

        Assert.Equal([3, 1, 2], board.In(SessionGroup.NeedsAnswer).Select(n => n.Session.Issue));
    }

    [Fact]
    public void Sessions_with_a_blocker_that_has_no_session_are_grouped_by_it()
    {
        var parked = new IssueStatus { Number = 9, Blocked = true };
        var board = SessionGroups.Build(
            [Session(1, "waiting", blockedBy: 9), Session(2, "waiting", blockedBy: 9)],
            [new RepoIssues { Repo = "o/a", Items = [parked] }]);

        var outside = Assert.Single(board.Outside);
        Assert.Same(parked, outside.Issue);
        Assert.Equal(2, outside.Waiting.Count);
        Assert.Empty(board.Roots);
    }

    [Fact]
    public void A_waiting_session_without_a_named_blocker_is_listed_as_unknown()
    {
        var outside = Assert.Single(Build(Session(1, "waiting")).Outside);

        Assert.Null(outside.Number);
        Assert.Null(outside.Issue);
    }

    [Fact]
    public void Sessions_that_wait_on_each_other_are_listed_once_and_marked()
    {
        var board = Build(Session(1, "waiting", blockedBy: 2), Session(2, "waiting", blockedBy: 1), Session(3, "waiting", blockedBy: 2));

        var root = Assert.Single(board.In(SessionGroup.Waiting));
        Assert.Equal([1, 2, 3], root.Tree.Select(n => n.Session.Issue));
        Assert.Equal([true, true, false], root.Tree.Select(n => n.InCycle));
        Assert.Empty(board.Outside);
    }
}
