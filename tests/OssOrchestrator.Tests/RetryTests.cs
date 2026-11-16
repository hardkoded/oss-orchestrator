using System.Net;
using Microsoft.Azure.Cosmos;
using OssOrchestrator.Agent;

namespace OssOrchestrator.Tests;

public class RetryTests
{
    private static CosmosException Failure(HttpStatusCode status) => new("failed", status, 0, "", 0);

    [Fact]
    public async Task Retries_a_timeout_until_the_write_succeeds()
    {
        var calls = 0;
        await Retry.WriteAsync(() => ++calls < 3 ? Task.FromException(Failure(HttpStatusCode.RequestTimeout)) : Task.CompletedTask, delayMs: 0);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt()
    {
        var calls = 0;
        await Assert.ThrowsAsync<CosmosException>(() =>
            Retry.WriteAsync(() => { calls++; return Task.FromException(Failure(HttpStatusCode.ServiceUnavailable)); }, attempts: 3, delayMs: 0));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Does_not_retry_other_failures()
    {
        var calls = 0;
        await Assert.ThrowsAsync<CosmosException>(() =>
            Retry.WriteAsync(() => { calls++; return Task.FromException(Failure(HttpStatusCode.Forbidden)); }, delayMs: 0));
        Assert.Equal(1, calls);
    }
}
