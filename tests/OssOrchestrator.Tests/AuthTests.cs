using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OssOrchestrator.Tests;

public class AuthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Health_is_anonymous()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_requires_sign_in()
    {
        var response = await _client.GetAsync("/");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
