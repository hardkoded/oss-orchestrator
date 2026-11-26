using System.Text.Json;
using OssOrchestrator.Web;
using OssOrchestrator.Web.Data;

namespace OssOrchestrator.Tests;

public class ButtonTests
{
    private static ConfigDoc Config(bool paused, bool auto) => new() { Repos = [new RepoDoc { Repo = "o/a", Paused = paused, Auto = auto }] };

    [Theory]
    [InlineData(true, false, "true", "resume,auto")]
    [InlineData(true, true, "true", "resume")]
    [InlineData(false, false, "true", "auto")]
    [InlineData(false, true, "true", "")]
    [InlineData(false, true, "false", "auto")]
    public void The_take_switch_sends_what_the_agent_needs(bool paused, bool auto, string value, string expected)
    {
        var (commands, error) = CommandFactory.FromButton($"take|o/a||{value}", Config(paused, auto), "me");

        Assert.Null(error);
        Assert.Equal(expected, string.Join(',', commands.Select(c => c.Type)));
    }

    [Fact]
    public void A_button_names_its_repo_issue_and_value()
    {
        var park = Assert.Single(CommandFactory.FromButton("block_session|o/a|7", null, "me").Commands);
        Assert.Equal(("o/a", 7), (park.Repo, park.Issue));
        var limit = Assert.Single(CommandFactory.FromButton("limit|||8", null, "me").Commands);
        Assert.Equal(8, limit.Value);
        Assert.Null(limit.Repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("drop_database")]
    [InlineData("limit|||21")]
    [InlineData("take|o/unknown||true")]
    public void A_bad_button_queues_nothing(string? button)
    {
        var (commands, error) = CommandFactory.FromButton(button, Config(false, false), "me");

        Assert.Empty(commands);
        Assert.NotNull(error);
    }

    [Fact]
    public void Activity_reads_a_value_that_came_back_from_the_database()
    {
        var command = new CommandDoc { Type = "auto", Repo = "o/a", Value = JsonSerializer.SerializeToElement(true) };

        Assert.Equal("Take new issues from a", Activity.Describe(command));
    }
}
