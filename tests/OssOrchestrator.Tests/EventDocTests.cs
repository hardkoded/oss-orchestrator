using OssOrchestrator.Agent;

namespace OssOrchestrator.Tests;

public class EventDocTests
{
    [Fact]
    public void Log_text_names_repo_type_action_and_number() =>
        Assert.Equal("a/b issues closed #7", new EventDoc { Repo = "a/b", Type = "issues", Action = "closed", Number = 7 }.ToString());

    [Fact]
    public void Log_text_skips_missing_parts() =>
        Assert.Equal("a/b push", new EventDoc { Repo = "a/b", Type = "push" }.ToString());
}
