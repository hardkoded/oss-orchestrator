using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OssOrchestrator.Web;

namespace OssOrchestrator.Tests;

public class WebhookTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string Sign(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void Signature_must_match_the_body_and_secret()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        Assert.True(Webhooks.IsValidSignature(body, Sign(body, "s3cret"), "s3cret"));
        Assert.False(Webhooks.IsValidSignature(body, Sign(body, "other"), "s3cret"));
        Assert.False(Webhooks.IsValidSignature(Encoding.UTF8.GetBytes("{ }"), Sign(body, "s3cret"), "s3cret"));
        Assert.False(Webhooks.IsValidSignature(body, null, "s3cret"));
        Assert.False(Webhooks.IsValidSignature(body, "sha256=", "s3cret"));
    }

    [Fact]
    public void Closed_issue_becomes_an_event_with_the_delivery_id()
    {
        var e = Webhooks.ToEvent("d-1", "issues",
            Json("""{"action":"closed","issue":{"number":7},"repository":{"full_name":"a/b"}}"""));
        Assert.NotNull(e);
        Assert.Equal(("d-1", "a/b", "issues", "closed", 7), (e.Id, e.Repo, e.Type, e.Action, e.Number));
    }

    [Fact]
    public void Merged_pull_request_becomes_an_event()
    {
        var e = Webhooks.ToEvent("d-2", "pull_request",
            Json("""{"action":"closed","pull_request":{"number":12,"merged":true},"repository":{"full_name":"a/b"}}"""));
        Assert.Equal(12, e?.Number);
    }

    [Fact]
    public void Push_counts_only_on_the_default_branch()
    {
        const string Repo = "\"repository\":{\"full_name\":\"a/b\",\"default_branch\":\"main\"}";
        Assert.NotNull(Webhooks.ToEvent("d-3", "push", Json("{\"ref\":\"refs/heads/main\"," + Repo + "}")));
        Assert.Null(Webhooks.ToEvent("d-4", "push", Json("{\"ref\":\"refs/heads/feature\"," + Repo + "}")));
    }

    [Theory]
    [InlineData("issues", """{"action":"labeled","issue":{"number":1},"repository":{"full_name":"a/b"}}""")]
    [InlineData("pull_request", """{"action":"labeled","pull_request":{"number":1},"repository":{"full_name":"a/b"}}""")]
    [InlineData("workflow_run", """{"action":"requested","repository":{"full_name":"a/b"}}""")]
    [InlineData("ping", """{"zen":"hi","repository":{"full_name":"a/b"}}""")]
    [InlineData("issues", """{"action":"closed"}""")]
    public void Other_events_are_ignored(string eventName, string payload) =>
        Assert.Null(Webhooks.ToEvent("d", eventName, Json(payload)));

    [Fact]
    public async Task Endpoint_is_closed_without_a_secret()
    {
        var response = await factory.CreateClient().PostAsync("/webhooks/github", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_rejects_a_bad_signature()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("GitHub:WebhookSecret", "s3cret")).CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github") { Content = new StringContent("{}") };
        request.Headers.Add("X-Hub-Signature-256", "sha256=00");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }
}
