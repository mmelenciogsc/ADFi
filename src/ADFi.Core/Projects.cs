namespace ADFi.Core;

public enum ProjectPublicationState
{
    Draft,
    Private,
    Unlisted,
    CommunityActive,
    ClaimedActive,
    ClaimDisputed,
    Blocked,
    TakedownPending,
    Removed,
    Restored,
    Archived,
}

public sealed record ProjectAvailability(
    bool Playable,
    bool Discoverable,
    bool Shareable,
    bool RevenueEligible);

public static class ProjectAvailabilityPolicy
{
    public static ProjectAvailability For(ProjectPublicationState state) => state switch
    {
        ProjectPublicationState.CommunityActive => new(true, true, true, true),
        ProjectPublicationState.ClaimedActive => new(true, true, true, true),
        ProjectPublicationState.ClaimDisputed => new(true, false, false, false),
        ProjectPublicationState.Restored => new(true, true, true, true),
        ProjectPublicationState.Unlisted => new(true, false, false, false),
        ProjectPublicationState.Private => new(false, false, false, false),
        ProjectPublicationState.Draft => new(false, false, false, false),
        ProjectPublicationState.Blocked => new(false, false, false, false),
        ProjectPublicationState.TakedownPending => new(false, false, false, false),
        ProjectPublicationState.Removed => new(false, false, false, false),
        ProjectPublicationState.Archived => new(false, false, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}

