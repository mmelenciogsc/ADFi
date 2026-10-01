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
    bool CanParticipateEconomically);

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

