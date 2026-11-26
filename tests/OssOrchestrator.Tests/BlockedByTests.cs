using System.Text.Json;
using OssOrchestrator.Agent;

namespace OssOrchestrator.Tests;

public class BlockedByTests
{
    private static readonly HashSet<string> Own = new(StringComparer.OrdinalIgnoreCase) { "worker" };

    private static readonly DateTimeOffset Noon = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("labeled", "needs-answer", -60, true)]
    [InlineData("labeled", "needs-answer", 60, false)]
    [InlineData("unlabeled", "waiting-on", -60, true)]
    [InlineData("unlabeled", "waiting-on", 60, false)]
    [InlineData("unlabeled", "needs-answer", 60, true)]
    [InlineData("labeled", "waiting-on", 60, true)]
    [InlineData("labeled", "in-progress", 60, false)]
    [InlineData("labeled", "in-progress", -60, true)]
    [InlineData("unlabeled", "Waiting-On", 60, false)]
    public void A_marker_starts_a_wait_unless_a_question_or_the_end_of_a_wait_came_after_it(string action, string label, int seconds, bool expected)
    {
        Assert.Equal(expected, BlockedBy.IsNewWait(Noon, [(action, label, Noon.AddSeconds(seconds))]));
    }

    [Fact]
    public void The_time_is_that_of_the_newest_marker_comment()
    {
        var issue = JsonSerializer.SerializeToElement(new
        {
            comments = new[]
            {
                new { author = new { login = "worker" }, body = "<!-- orc:blocked-by #12 -->", createdAt = Noon },
                new { author = new { login = "worker" }, body = "No marker", createdAt = Noon.AddHours(1) },
            },
        });
        Assert.Equal(Noon, BlockedBy.MarkedAt(issue, 1, Own));
        Assert.Null(BlockedBy.MarkedAt(Comments(("worker", "No marker")), 1, Own));
    }

    private static JsonElement Comments(params (string Login, string Body)[] items) =>
        JsonSerializer.SerializeToElement(new
        {
            comments = items.Select(i => new { author = new { login = i.Login }, body = i.Body }),
        });

    [Fact]
    public void The_newest_marker_comment_by_the_worker_lists_every_blocker()
    {
        var issue = Comments(
            ("worker", "Waiting. <!-- orc:blocked-by #12 -->"),
            ("worker", "Now on <!-- orc:blocked-by #15 --> <!-- orc:blocked-by #13 --> <!-- orc:blocked-by #15 -->"),
            ("worker", "A comment without a marker"));
        Assert.Equal([15, 13], BlockedBy.FromComments(issue, 1, Own));
    }

    [Fact]
    public void A_marker_by_someone_else_is_ignored()
    {
        var issue = Comments(("stranger", "<!-- orc:blocked-by #12 -->"));
        Assert.Empty(BlockedBy.FromComments(issue, 1, Own));
    }

    [Theory]
    [InlineData("no marker here")]
    [InlineData("<!-- orc:blocked-by owner/other#12 -->")]
    [InlineData("<!-- orc:blocked-by twelve -->")]
    [InlineData("<!-- orc:blocked-by #1 -->")]   // the issue itself
    public void Text_that_is_not_a_same_repo_number_is_ignored(string body)
    {
        Assert.Empty(BlockedBy.FromComments(Comments(("worker", body)), 1, Own));
    }

    [Fact]
    public void An_issue_without_comments_has_no_blocker()
    {
        Assert.Empty(BlockedBy.FromComments(JsonSerializer.SerializeToElement(new { }), 1, Own));
    }

    [Theory]
    [InlineData("""{"state":"open"}""", BlockerState.Open)]
    [InlineData("""{"state":"closed"}""", BlockerState.Closed)]
    [InlineData("""{"state":"closed","pull_request":{"merged_at":null}}""", BlockerState.Closed)]
    [InlineData("""{"state":"closed","pull_request":{"merged_at":"2026-11-26T10:00:00Z"}}""", BlockerState.Done)]
    [InlineData("""{"state":"closed","state_reason":"completed"}""", BlockerState.Done)]
    [InlineData("""{"state":"closed","state_reason":"not_planned"}""", BlockerState.Closed)]
    public void Reads_whether_the_blocker_is_open_merged_or_closed(string json, BlockerState expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, GitHub.ReadBlockerState(doc.RootElement));
    }

    [Fact]
    public void The_resume_message_tells_a_merge_from_a_close()
    {
        var message = BlockedBy.ResumeMessage([(12, BlockerState.Done), (13, BlockerState.Closed), (14, BlockerState.Done)]);
        Assert.Contains("Done (merged or completed): #12, #14.", message);
        Assert.Contains("closed as not planned): #13.", message);
        Assert.DoesNotContain("Closed without", BlockedBy.ResumeMessage([(12, BlockerState.Done)]));
    }
}
