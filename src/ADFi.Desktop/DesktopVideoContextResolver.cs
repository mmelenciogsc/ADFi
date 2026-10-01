using ADFi.Core;

namespace ADFi.Desktop;

public sealed class DesktopVideoContextResolver(AdfiApiClient apiClient) : IVideoContextResolver
{
    public async Task<VideoContextResolution> ResolveAsync(
        InteractiveVideoSource source,
        long currentTimestampMs,
        MomentEvidenceRequirement requirement,
        CancellationToken cancellationToken)
    {
        var context = source.ProjectId is { } projectId
            ? await apiClient.GetDiscoverMomentContextAsync(
                projectId,
                currentTimestampMs,
                cancellationToken).ConfigureAwait(false)
            : source.YouTubeVideoId is { } youtubeVideoId
                ? await apiClient.GetDiscoverMomentContextByYoutubeIdAsync(
                    youtubeVideoId,
                    currentTimestampMs,
                    cancellationToken).ConfigureAwait(false)
                : null;
        if (context is null)
        {
            return new(null, [], [], [], [], false);
        }

        var canAnswer = requirement.Intent switch
        {
            MomentQuestionIntent.ExactText => context.OcrContext.Count > 0,
            MomentQuestionIntent.PersonContinuity => context.EstablishedEntities.Count > 0,
            MomentQuestionIntent.Speaker => context.DialogueContext.Count > 0,
            _ => context.ExistingEvidence.Count > 0,
        };
        return context with { ExistingEvidenceCanAnswer = canAnswer };
    }
}
