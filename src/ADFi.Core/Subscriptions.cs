namespace ADFi.Core;

public enum SubscriptionPlan
{
    None = 0,
    Community = 1,
    Creator = 2,
}

public sealed record PlanDefinition(
    SubscriptionPlan Plan,
    string DisplayName,
    int MonthlyPriceUsdCents,
    bool CanCreateAdfiedMedia,
    bool CanUseNaturalConversationEditing,
    bool CanParticipateEconomically,
    bool CanWatchAndAsk = true);

public static class PlanCatalog
{
    public static IReadOnlyList<PlanDefinition> PaidPlans { get; } =
    [
        new(SubscriptionPlan.Community, "Community", 200, false, false, true),
        new(SubscriptionPlan.Creator, "Creator", 900, true, true, true),
    ];

    public static PlanDefinition Get(SubscriptionPlan plan) =>
        PaidPlans.Single(candidate => candidate.Plan == plan);
}

public static class Entitlements
{
    public static bool CanOpenDiscover(SubscriptionPlan plan) => true;

    public static bool CanCreate(SubscriptionPlan plan) => plan == SubscriptionPlan.Creator;

    public static bool CanEditConversationally(SubscriptionPlan plan) =>
        plan == SubscriptionPlan.Creator;

    public static bool CanUseCommunityVqa(SubscriptionPlan plan) =>
        plan is SubscriptionPlan.Community or SubscriptionPlan.Creator;

    public static bool CanParticipateEconomically(SubscriptionPlan plan) =>
        plan is SubscriptionPlan.Community or SubscriptionPlan.Creator;
}

/// <summary>
/// Fail-closed desktop gating for local Creator operations. A cached account identity is not
/// sufficient: the backend must have verified the current session and active Creator entitlement.
/// </summary>
public static class CreatorToolAccess
{
    public static bool CanSelectLocalSource(
        SubscriptionPlan plan,
        bool entitlementVerified,
        bool rightsAccepted) =>
        entitlementVerified
        && Entitlements.CanCreate(plan)
        && rightsAccepted;

    public static bool CanUseConversationReview(
        SubscriptionPlan plan,
        bool entitlementVerified,
        bool projectSelected) =>
        entitlementVerified
        && Entitlements.CanEditConversationally(plan)
        && projectSelected;
}
