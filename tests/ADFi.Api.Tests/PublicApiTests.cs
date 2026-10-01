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
    public async Task PublicDiscoverDoesNotRequireAuthentication()
    {
        using var response = await client.GetAsync(
            "/api/v1/discover",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
