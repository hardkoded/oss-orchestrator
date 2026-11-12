using System.Text.Json;
using OssOrchestrator.Agent;
using Xunit;

namespace OssOrchestrator.Tests;

public class PrStatusTests
{
    private static JsonElement Checks(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Conflicting_PR_is_conflicted_even_when_checks_pass()
    {
        var rollup = Checks("""[{"status":"COMPLETED","conclusion":"SUCCESS"}]""");
        Assert.Equal("conflicted", PrStatus.From("CONFLICTING", rollup));
    }

    [Fact]
    public void No_checks_yet_is_pending()
    {
        Assert.Equal("pending", PrStatus.From("MERGEABLE", Checks("[]")));
    }

    [Fact]
    public void All_checks_passing_is_ready()
    {
        var rollup = Checks("""[{"status":"COMPLETED","conclusion":"SUCCESS"},{"status":"COMPLETED","conclusion":"SKIPPED"}]""");
        Assert.Equal("ready", PrStatus.From("MERGEABLE", rollup));
    }

    [Fact]
    public void A_check_still_running_is_building()
    {
        var rollup = Checks("""[{"status":"COMPLETED","conclusion":"SUCCESS"},{"status":"IN_PROGRESS","conclusion":""}]""");
        Assert.Equal("building", PrStatus.From("MERGEABLE", rollup));
    }

    [Fact]
    public void A_failed_check_is_failed_even_while_others_run()
    {
        var rollup = Checks("""[{"status":"COMPLETED","conclusion":"FAILURE"},{"status":"IN_PROGRESS","conclusion":""}]""");
        Assert.Equal("failed", PrStatus.From("MERGEABLE", rollup));
    }

    [Theory]
    [InlineData("fix/1531-short-name", 1531)]
    [InlineData("feature/2-x", 2)]
    [InlineData("main", null)]
    public void Issue_number_comes_from_the_branch_name(string branch, int? expected)
    {
        Assert.Equal(expected, GitHub.IssueFromBranch(branch));
    }

    [Fact]
    public void Legacy_status_contexts_are_read_by_state()
    {
        Assert.Equal("failed", PrStatus.From("MERGEABLE", Checks("""[{"state":"ERROR"}]""")));
        Assert.Equal("building", PrStatus.From("MERGEABLE", Checks("""[{"state":"PENDING"}]""")));
        Assert.Equal("ready", PrStatus.From("MERGEABLE", Checks("""[{"state":"SUCCESS"}]""")));
    }
}
