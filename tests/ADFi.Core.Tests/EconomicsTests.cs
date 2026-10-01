using ADFi.Economics;

namespace ADFi.Core.Tests;

public sealed class EconomicsTests
{
    [Fact]
    public void NetRevenueUsesOnlyPermittedDeductionsAndAllocatesExactlySeventyThirty()
    {
        var result = RevenuePolicy.Calculate(new(100m, 3m, 2m, 4m, 1m));

        Assert.Equal(90m, result.NetSubscriptionRevenueUsd);
        Assert.Equal(63m, result.CommunityEconomyUsd);
        Assert.Equal(27m, result.AdfiUsd);
        Assert.Equal(result.NetSubscriptionRevenueUsd, result.CommunityEconomyUsd + result.AdfiUsd);
    }

    [Fact]
    public void DeductionsCannotCreateNegativeNetRevenue()
    {
        var result = RevenuePolicy.Calculate(new(10m, 5m, 5m, 5m, 5m));
        Assert.Equal(0m, result.NetSubscriptionRevenueUsd);
    }
}

