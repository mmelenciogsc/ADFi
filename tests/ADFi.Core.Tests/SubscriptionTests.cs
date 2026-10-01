using ADFi.Core;

namespace ADFi.Core.Tests;

public sealed class SubscriptionTests
{
    [Fact]
    public void CatalogContainsExactlyTheTwoPaidPlans()
    {
        Assert.Collection(
            PlanCatalog.PaidPlans,
            community =>
            {
                Assert.Equal(SubscriptionPlan.Community, community.Plan);
                Assert.Equal(200, community.MonthlyPriceUsdCents);
                Assert.False(community.CanCreateAdfiedMedia);
                Assert.False(community.CanUseNaturalConversationEditing);
                Assert.True(community.CanParticipateEconomically);
            },
            creator =>
            {
                Assert.Equal(SubscriptionPlan.Creator, creator.Plan);
                Assert.Equal(900, creator.MonthlyPriceUsdCents);
                Assert.True(creator.CanCreateAdfiedMedia);
                Assert.True(creator.CanUseNaturalConversationEditing);
                Assert.True(creator.CanParticipateEconomically);
            });
    }

    [Theory]
    [InlineData(SubscriptionPlan.None, false, false)]
    [InlineData(SubscriptionPlan.Community, false, false)]
    [InlineData(SubscriptionPlan.Creator, true, true)]
    public void CreatorCapabilitiesAreServerAuthoritativeByPlan(
        SubscriptionPlan plan,
        bool canCreate,
        bool canEdit)
    {
        Assert.Equal(canCreate, Entitlements.CanCreate(plan));
        Assert.Equal(canEdit, Entitlements.CanEditConversationally(plan));
    }

    [Fact]
    public void DiscoverIsPublic()
    {
        Assert.True(Entitlements.CanOpenDiscover(SubscriptionPlan.None));
    }
}

