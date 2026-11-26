using System.Text.Json;
using OssOrchestrator.Agent;

namespace OssOrchestrator.Tests;

public class ApplierTests
{
    private static CommandDoc Command(string type, int value) => new() { Type = type, Value = JsonSerializer.SerializeToElement(value) };

    [Fact]
    public void Lowering_max_sessions_lowers_the_work_slots_too()
    {
        var config = new ConfigDoc { MaxTotal = 6, MaxSessions = 20 };

        Applier.Apply(config, Command("sessions", 4));

        Assert.Equal((4, 4), (config.MaxSessions, config.MaxTotal));
    }

    [Theory]
    [InlineData("sessions", 0)]
    [InlineData("sessions", 21)]
    [InlineData("limit", 11)]
    public void A_value_out_of_range_changes_nothing(string type, int value)
    {
        var config = new ConfigDoc { MaxTotal = 6, MaxSessions = 10 };

        var result = Applier.Apply(config, Command(type, value));

        Assert.Equal((10, 6), (config.MaxSessions, config.MaxTotal));
        Assert.NotNull(OssOrchestrator.Web.Activity.Problem(new OssOrchestrator.Web.Data.CommandDoc { Result = result }));
    }
}
