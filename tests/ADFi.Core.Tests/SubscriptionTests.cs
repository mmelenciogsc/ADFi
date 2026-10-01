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
                Assert.True(community.CanWatchAndAsk);
            },
            creator =>
            {
                Assert.Equal(SubscriptionPlan.Creator, creator.Plan);
                Assert.Equal(900, creator.MonthlyPriceUsdCents);
                Assert.True(creator.CanCreateAdfiedMedia);
                Assert.True(creator.CanUseNaturalConversationEditing);
                Assert.True(creator.CanParticipateEconomically);
                Assert.True(creator.CanWatchAndAsk);
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

    [Theory]
    [InlineData(SubscriptionPlan.None, true, true, false)]
    [InlineData(SubscriptionPlan.Community, true, true, false)]
    [InlineData(SubscriptionPlan.Creator, false, true, false)]
    [InlineData(SubscriptionPlan.Creator, true, false, false)]
    [InlineData(SubscriptionPlan.Creator, true, true, true)]
    public void LocalSourceSelectionFailsClosedUntilEveryRequirementIsSatisfied(
        SubscriptionPlan plan,
        bool entitlementVerified,
        bool rightsAccepted,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreatorToolAccess.CanSelectLocalSource(plan, entitlementVerified, rightsAccepted));
    }

    [Theory]
    [InlineData(SubscriptionPlan.Community, true, true, false)]
    [InlineData(SubscriptionPlan.Creator, false, true, false)]
    [InlineData(SubscriptionPlan.Creator, true, false, false)]
    [InlineData(SubscriptionPlan.Creator, true, true, true)]
    public void ConversationReviewRequiresVerifiedCreatorAndSelectedProject(
        SubscriptionPlan plan,
        bool entitlementVerified,
        bool projectSelected,
        bool expected)
    {
        Assert.Equal(
            expected,
            CreatorToolAccess.CanUseConversationReview(plan, entitlementVerified, projectSelected));
    }
}
