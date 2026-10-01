using ADFi.Core;

namespace ADFi.YouTube;

public sealed record YouTubeStatusDecision(
    ProjectPublicationState ProjectState,
    ProjectAvailability Availability,
    string ReasonCode);

/// <summary>
/// Converts provider availability into ADFi state without treating an ordinary monetization
/// claim as a takedown. YouTube's Data API does not expose every rights claim, so an existing,
/// processed, embeddable asset stays available unless a separately verified rights event says otherwise.
/// </summary>
public static class YouTubeStatusPolicy
{
    public static YouTubeStatusDecision Evaluate(
        YouTubeAssetStatus status,
        ProjectPublicationState currentState)
    {
        ArgumentNullException.ThrowIfNull(status);
        ProjectPublicationState state;
        string reason;
        if (!status.Exists || string.Equals(status.UploadStatus, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            state = ProjectPublicationState.Removed;
            reason = "YOUTUBE_ASSET_REMOVED";
        }
        else if (string.Equals(status.UploadStatus, "failed", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(status.UploadStatus, "rejected", StringComparison.OrdinalIgnoreCase))
        {
            state = ProjectPublicationState.Blocked;
            reason = "YOUTUBE_UPLOAD_BLOCKED";
        }
        else if (!status.Embeddable)
        {
            state = ProjectPublicationState.Blocked;
            reason = "YOUTUBE_EMBED_DISABLED";
        }
        else if (status.Visibility == YouTubeVisibility.Private)
        {
            state = ProjectPublicationState.Private;
            reason = "YOUTUBE_PRIVATE";
        }
        else if (status.Visibility == YouTubeVisibility.Unlisted)
        {
            state = ProjectPublicationState.Unlisted;
            reason = "YOUTUBE_UNLISTED";
        }
        else if (!string.Equals(status.UploadStatus, "processed", StringComparison.OrdinalIgnoreCase))
        {
            state = ProjectPublicationState.Unlisted;
            reason = "YOUTUBE_PROCESSING";
        }
        else if (currentState is ProjectPublicationState.ClaimedActive
                 or ProjectPublicationState.ClaimDisputed
                 or ProjectPublicationState.TakedownPending)
        {
            state = currentState;
            reason = "ADFI_RIGHTS_STATE_PRESERVED";
        }
        else
        {
            state = ProjectPublicationState.CommunityActive;
            reason = "YOUTUBE_PUBLIC_AND_PLAYABLE";
        }

        return new(state, ProjectAvailabilityPolicy.For(state), reason);
    }
}
