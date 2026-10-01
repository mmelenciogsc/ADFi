using System.Net;
using System.Net.Http.Json;
using ADFi.Core;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ADFi.Api.Tests;

public sealed class PublicApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;

    public PublicApiTests(WebApplicationFactory<Program> factory)
    {
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
    }

    [Fact]
    public async Task LivenessDoesNotDependOnPostgres()
    {
        using var response = await client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessFailsClearlyWhenPostgresIsNotConfigured()
    {
        using var response = await client.GetAsync(
            "/health/ready",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PublicPlanEndpointReturnsExactlyCommunityAndCreator()
    {
        var plans = await client.GetFromJsonAsync<PlanDefinition[]>(
            "/api/v1/plans",
            TestContext.Current.CancellationToken);

        Assert.NotNull(plans);
        Assert.Equal([SubscriptionPlan.Community, SubscriptionPlan.Creator], plans.Select(plan => plan.Plan));
        Assert.Equal([200, 900], plans.Select(plan => plan.MonthlyPriceUsdCents));
    }

    [Fact]
    public async Task PublicDiscoverFailsClearlyWhenPersistenceIsUnavailable()
    {
        using var response = await client.GetAsync(
            "/api/v1/discover",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PublicDiscoverUnavailablePageUsesAccessibleLandmarksWithoutLogin()
    {
        using var response = await client.GetAsync(
            "/discover",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("<main id=\"main\">", html, StringComparison.Ordinal);
        Assert.Contains("<h1>DISCOVER</h1>", html, StringComparison.Ordinal);
        Assert.Contains("temporarily unavailable", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/subscriptions/entitlements")]
    [InlineData("/api/v1/subscriptions/checkout")]
    [InlineData("/api/v1/paypal/webhook")]
    public async Task PaymentEndpointsFailClosedWhenServerSecretsAreAbsent(string path)
    {
        using var request = new HttpRequestMessage(
            path.EndsWith("entitlements", StringComparison.Ordinal) ? HttpMethod.Get : HttpMethod.Post,
            path);
        if (request.Method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PublicResponseIncludesSecurityHeaders()
    {
        using var response = await client.GetAsync("/discover", TestContext.Current.CancellationToken);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("youtube-nocookie.com", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }
}
