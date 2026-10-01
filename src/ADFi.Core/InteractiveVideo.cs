using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ADFi.Core;

[JsonConverter(typeof(JsonStringEnumConverter<InteractiveVideoSourceType>))]
public enum InteractiveVideoSourceType
{
    LocalFile,
    AdfiPublication,
    YouTubeStream,
}

[JsonConverter(typeof(JsonStringEnumConverter<InteractivePlaybackState>))]
public enum InteractivePlaybackState
{
    Unavailable,
    Stopped,
    Playing,
    Paused,
    Ended,
}

[JsonConverter(typeof(JsonStringEnumConverter<MomentQuestionIntent>))]
public enum MomentQuestionIntent
{
    StaticVisual,
    Action,
    Change,
    ExactText,
    PersonContinuity,
    Speaker,
    SpatialRelationship,
    RecentContext,
}

[JsonConverter(typeof(JsonStringEnumConverter<MomentEvidenceKind>))]
public enum MomentEvidenceKind
{
    PersistedSceneAnalysis,
    PersistedOcr,
    PersistedEntity,
    PersistedAudioDescription,
    PersistedTranscript,
    EphemeralImage,
    UserProvidedContext,
    OfficialVideoMetadata,
}

[JsonConverter(typeof(JsonStringEnumConverter<MomentEvidenceBasis>))]
public enum MomentEvidenceBasis
{
    ExistingAdfiAnalysis,
    NewLocalFrameAnalysis,
    NewYoutubeEphemeralCapture,
    UserProvidedContext,
    OfficialVideoMetadata,
    NoDirectVisualEvidence,
}

[JsonConverter(typeof(JsonStringEnumConverter<VqaProviderKind>))]
public enum VqaProviderKind
{
    ChatGpt,
    LocalFirst,
    OtherUserOwnedProvider,
}

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceRetentionPolicy>))]
public enum EvidenceRetentionPolicy
{
    DeleteAfterRequest,
    RetainDuringActiveQuestionTransaction,
    ShortPrivateAccessibilityHistory,
}

[JsonConverter(typeof(JsonStringEnumConverter<CaptureComplianceState>))]
public enum CaptureComplianceState
{
    NotRequired,
    AllowedLocalFile,
    ExistingAdfiEvidenceOnly,
    YoutubeCaptureDisabled,
    YoutubeCaptureEnabledAfterApproval,
    EvidenceUnavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<VqaProviderAvailabilityState>))]
public enum VqaProviderAvailabilityState
{
    NotConnected,
    ConnectedWithoutPlanUsage,
    PlanUsageAvailable,
    LocalReady,
    LimitReached,
    ReauthorizationRequired,
    ProviderApprovalRequired,
    Unavailable,
}

public sealed record InteractiveVideoSource(
    InteractiveVideoSourceType SourceType,
    string SourceReference,
    [property: JsonIgnore] string? PrivateLocalPath,
    Guid? ProjectId,
    Guid? PublicationId,
    string? YouTubeVideoId)
{
    public static InteractiveVideoSource LocalFile(string privateLocalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateLocalPath);
        return new(
            InteractiveVideoSourceType.LocalFile,
            $"local:{Guid.NewGuid():N}",
            Path.GetFullPath(privateLocalPath),
            null,
            null,
            null);
    }

    public static InteractiveVideoSource AdfiPublication(
        Guid projectId,
        Guid publicationId,
        string youtubeVideoId) =>
        new(
            InteractiveVideoSourceType.AdfiPublication,
            $"adfi:{publicationId:D}",
            null,
            projectId,
            publicationId,
            YoutubeVideoUrl.TryParseVideoId(youtubeVideoId, out var normalized)
                ? normalized
                : throw new ArgumentException("A valid YouTube video ID is required.", nameof(youtubeVideoId)));

    public static InteractiveVideoSource YouTube(string url)
    {
        if (!YoutubeVideoUrl.TryParseVideoUrl(url, out var videoId))
        {
            throw new ArgumentException("Enter a supported youtube.com or youtu.be video URL.", nameof(url));
        }

        return new(
            InteractiveVideoSourceType.YouTubeStream,
            $"youtube:{videoId}",
            null,
            null,
            null,
            videoId);
    }
}

public sealed record PlaybackCheckpoint(
    Guid SessionId,
    InteractiveVideoSourceType SourceType,
    string SourceReference,
    Guid? ProjectId,
    Guid? PublicationId,
    string? YouTubeVideoId,
    long CurrentTimestampMs,
    InteractivePlaybackState PreviousState,
    decimal PlaybackRate,
    string? SelectedCaptionsState,
    string FocusOrigin,
    Guid VqaRequestId,
    DateTimeOffset CreatedUtc);

public sealed record PlaybackSnapshot(
    InteractivePlaybackState State,
    long CurrentTimestampMs,
    decimal PlaybackRate,
    string? SelectedCaptionsState);

public sealed record MomentEvidenceRequirement(
    MomentQuestionIntent Intent,
    int MaximumImageCount,
    int TemporalSpanMs,
    bool RequiresVisualEvidence,
    bool RequiresExactText,
    bool IncludeDialogueWhenAvailable,
    bool PreferPersistedAdfiEvidence,
    string Reason);

public sealed record MomentVqaEvidence(
    string EvidenceId,
    MomentEvidenceKind Kind,
    MomentEvidenceBasis Basis,
    long StartTimestampMs,
    long EndTimestampMs,
    string? Text,
    [property: JsonIgnore] string? PrivateEphemeralPath,
    int? PixelWidth,
    int? PixelHeight,
    decimal Confidence,
    bool Ephemeral);

public sealed record AdfiMomentSceneContext(
    string SceneId,
    long StartTimestampMs,
    long EndTimestampMs,
    string? ObjectiveVisualSummary,
    IReadOnlyList<string> AdjacentSceneIds,
    decimal Confidence);

public sealed record EstablishedEntityContext(
    string EntityId,
    string DisplayName,
    string EvidenceProvenance,
    decimal Confidence);

public sealed record MomentVqaRequest(
    Guid SessionId,
    Guid RequestId,
    InteractiveVideoSourceType SourceType,
    string SourceReference,
    long CurrentTimestampMs,
    string Question,
    MomentQuestionIntent QuestionIntent,
    IReadOnlyList<MomentVqaEvidence> Evidence,
    AdfiMomentSceneContext? AdfiSceneContext,
    IReadOnlyList<EstablishedEntityContext> EstablishedEntities,
    IReadOnlyList<string> OcrContext,
    IReadOnlyList<string> DialogueContext,
    VqaProviderKind Provider,
    EvidenceRetentionPolicy RetentionPolicy,
    CaptureComplianceState CaptureComplianceState);

public sealed record MomentVqaResult(
    string Answer,
    IReadOnlyList<MomentEvidenceBasis> EvidenceBasis,
    decimal Confidence,
    IReadOnlyList<string> Uncertainties,
    string? SuggestedFollowup,
    VqaProviderKind Provider,
    DateTimeOffset CreatedUtc);

public sealed record VqaProviderAvailability(
    VqaProviderKind Provider,
    VqaProviderAvailabilityState State,
    bool CanSubmit,
    string AccessibleStatus,
    Uri? ManageUsageUri);

public sealed record ViewerVqaFeaturePolicy(
    bool ChatGptPlanUsageIntegrationEnabled,
    bool YoutubeEphemeralAccessibilityCaptureEnabled,
    int MaximumCaptureCount,
    int MaximumTemporalSpanMs,
    int MaximumImageDimension,
    int TransactionLifetimeSeconds,
    string AccessibilityStatus);

public sealed record VideoContextResolution(
    AdfiMomentSceneContext? Scene,
    IReadOnlyList<MomentVqaEvidence> ExistingEvidence,
    IReadOnlyList<EstablishedEntityContext> EstablishedEntities,
    IReadOnlyList<string> OcrContext,
    IReadOnlyList<string> DialogueContext,
    bool ExistingEvidenceCanAnswer);

public sealed record EvidenceAcquisitionContext(
    Guid SessionId,
    Guid RequestId,
    InteractiveVideoSource Source,
    long CurrentTimestampMs,
    MomentEvidenceRequirement Requirement,
    bool ExplicitUserAskAction,
    string SessionPrivateDirectory,
    DateTimeOffset CreatedUtc);

public interface IPlaybackController
{
    Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task SeekAsync(long timestampMs, CancellationToken cancellationToken);

    Task PlayAsync(CancellationToken cancellationToken);
}

public interface IVideoContextResolver
{
    Task<VideoContextResolution> ResolveAsync(
        InteractiveVideoSource source,
        long currentTimestampMs,
        MomentEvidenceRequirement requirement,
        CancellationToken cancellationToken);
}

public interface IVideoEvidenceProvider
{
    bool Supports(InteractiveVideoSourceType sourceType);

    Task<IReadOnlyList<MomentVqaEvidence>> AcquireAsync(
        EvidenceAcquisitionContext context,
        CancellationToken cancellationToken);
}

public interface IEphemeralEvidenceLifecycle
{
    string GetSessionDirectory(Guid sessionId);

    Task DeleteRequestAsync(Guid sessionId, Guid requestId, CancellationToken cancellationToken);

    Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken);
}

public interface IVqaProvider
{
    VqaProviderKind Kind { get; }

    Task<VqaProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    Task<MomentVqaResult> AskAsync(
        MomentVqaRequest request,
        CancellationToken cancellationToken);
}

public interface IInteractiveVideoSession : IAsyncDisposable
{
    Guid SessionId { get; }

    InteractiveVideoSource Source { get; }

    PlaybackCheckpoint? ActiveCheckpoint { get; }

    Task<PlaybackCheckpoint> PauseForQuestionAsync(
        string focusOrigin,
        CancellationToken cancellationToken);

    Task<MomentVqaResult> AskAsync(
        string question,
        VqaProviderKind provider,
        CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    Task CloseQuestionAsync(bool resumePlayback, CancellationToken cancellationToken);
}

public static class ViewerVqaAccessPolicy
{
    private static readonly HashSet<string> MutationTools = new(StringComparer.Ordinal)
    {
        "RewriteAdSegment",
        "ApplyProjectMutation",
        "SynthesizeNarration",
        "RenderFinalVideo",
        "PublishToYouTube",
    };

    public static bool CanUseViewer(SubscriptionPlan plan, bool entitlementVerified) =>
        entitlementVerified && Entitlements.CanUseCommunityVqa(plan);

    public static bool CanUseLocalFirst(SubscriptionPlan plan, bool entitlementVerified) =>
        entitlementVerified && plan == SubscriptionPlan.Creator;

    public static bool IsViewerToolAllowed(string toolName) =>
        !MutationTools.Contains(toolName);

    public static string ModeAnnouncement(bool creatorEditing) => creatorEditing
        ? "Creator editing — changes require validation and commit."
        : "Viewer — questions do not change this project.";
}

public sealed record MomentEvidenceLimits(
    int MaximumImageCount,
    int MaximumTemporalSpanMs,
    int MaximumImageDimension,
    int MaximumRecentContextMs)
{
    public static MomentEvidenceLimits Default { get; } = new(3, 1500, 1280, 10_000);

    public void Validate()
    {
        if (MaximumImageCount is < 1 or > 3)
        {
            throw new InvalidOperationException("Moment evidence is limited to one through three images.");
        }

        if (MaximumTemporalSpanMs is < 0 or > 2000)
        {
            throw new InvalidOperationException("Moment evidence temporal span cannot exceed two seconds.");
        }

        if (MaximumImageDimension is < 320 or > 1920)
        {
            throw new InvalidOperationException("Moment evidence image dimension is outside the safe range.");
        }

        if (MaximumRecentContextMs is < 1000 or > 15_000)
        {
            throw new InvalidOperationException("Recent-context evidence must remain bounded to fifteen seconds.");
        }
    }
}

public static partial class MomentQuestionClassifier
{
    public static MomentEvidenceRequirement Classify(
        string question,
        MomentEvidenceLimits? limits = null,
        int? elapsedSinceLastConfirmedPlaybackMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        limits ??= MomentEvidenceLimits.Default;
        limits.Validate();
        var normalized = WhitespaceRegex().Replace(question.Trim().ToLowerInvariant(), " ");

        if (ContainsAny(normalized, "what does", "what did the sign", "what does the sign", "read", "written", "text say", "number say"))
        {
            return new(
                MomentQuestionIntent.ExactText,
                1,
                0,
                true,
                true,
                false,
                true,
                "Use the current, highest-quality frame and exact-text evidence only.");
        }

        if (ContainsAny(normalized, "what did i miss", "what have i missed"))
        {
            var requestedSpan = Math.Clamp(
                elapsedSinceLastConfirmedPlaybackMs ?? limits.MaximumRecentContextMs,
                1000,
                limits.MaximumRecentContextMs);
            return new(
                MomentQuestionIntent.RecentContext,
                limits.MaximumImageCount,
                requestedSpan,
                true,
                false,
                true,
                true,
                "Use only the bounded period since the last confirmed playback point.");
        }

        if (ContainsAny(normalized, "what changed", "what is different", "what's different", "whats different"))
        {
            return Temporal(MomentQuestionIntent.Change, limits, "Compare only the immediately adjacent visual states.");
        }

        if (ContainsAny(normalized, "what's happening", "whats happening", "what is happening", "what did", "who just came", "doing now"))
        {
            return Temporal(MomentQuestionIntent.Action, limits, "Use a tiny temporal packet to resolve the current action.");
        }

        if (ContainsAny(normalized, "who is speaking", "who's speaking", "whos speaking", "who said"))
        {
            return new(
                MomentQuestionIntent.Speaker,
                1,
                0,
                false,
                false,
                true,
                true,
                "Prefer established entities and legitimate nearby dialogue metadata.");
        }

        if (ContainsAny(normalized, "who is she", "who is he", "who are they", "who's she", "who's he"))
        {
            return new(
                MomentQuestionIntent.PersonContinuity,
                1,
                0,
                true,
                false,
                false,
                true,
                "Use only names already established in this video; never perform external face identification.");
        }

        if (ContainsAny(normalized, "where is", "where are", "looking at", "describe the room", "describe this room"))
        {
            return new(
                MomentQuestionIntent.SpatialRelationship,
                1,
                0,
                true,
                false,
                false,
                true,
                "Use the current view for objective spatial relationships.");
        }

        return new(
            MomentQuestionIntent.StaticVisual,
            1,
            0,
            true,
            false,
            false,
            true,
            "Use the current representative view and no unrelated context.");
    }

    private static MomentEvidenceRequirement Temporal(
        MomentQuestionIntent intent,
        MomentEvidenceLimits limits,
        string reason) =>
        new(
            intent,
            limits.MaximumImageCount,
            limits.MaximumTemporalSpanMs,
            true,
            false,
            false,
            true,
            reason);

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record YoutubeEphemeralCapturePolicy(
    bool EnabledAfterComplianceApproval,
    MomentEvidenceLimits Limits,
    TimeSpan TransactionLifetime)
{
    public static YoutubeEphemeralCapturePolicy ProductionDefault { get; } =
        new(false, MomentEvidenceLimits.Default, TimeSpan.FromMinutes(2));

    public CaptureComplianceState Validate(EvidenceAcquisitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Limits.Validate();
        if (context.Source.SourceType != InteractiveVideoSourceType.YouTubeStream)
        {
            throw new InvalidOperationException("This policy applies only to arbitrary YouTube streams.");
        }

        if (!context.ExplicitUserAskAction)
        {
            throw new InvalidOperationException("YouTube visual evidence requires one explicit user Ask action.");
        }

        if (!EnabledAfterComplianceApproval)
        {
            return CaptureComplianceState.YoutubeCaptureDisabled;
        }

        if (context.Requirement.MaximumImageCount > Limits.MaximumImageCount
            || context.Requirement.TemporalSpanMs > Limits.MaximumTemporalSpanMs)
        {
            throw new InvalidOperationException("The requested YouTube evidence exceeds the compliance cap.");
        }

        if (context.CreatedUtc + TransactionLifetime < DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The private evidence transaction has expired.");
        }

        return CaptureComplianceState.YoutubeCaptureEnabledAfterApproval;
    }
}

public static partial class YoutubeVideoUrl
{
    public static bool TryParseVideoUrl(string value, out string videoId)
    {
        videoId = string.Empty;
        return Uri.TryCreate(value, UriKind.Absolute, out _)
               && TryParseVideoId(value, out videoId);
    }

    public static bool TryParseVideoId(string value, out string videoId)
    {
        videoId = string.Empty;
        if (VideoIdRegex().IsMatch(value))
        {
            videoId = value;
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        string? candidate = null;
        if (host is "youtu.be" or "www.youtu.be")
        {
            candidate = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtube-nocookie.com" or "www.youtube-nocookie.com")
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 1 && string.Equals(segments[0], "watch", StringComparison.OrdinalIgnoreCase))
            {
                candidate = ParseQuery(uri.Query).GetValueOrDefault("v");
            }
            else if (segments.Length >= 2
                     && segments[0] is "embed" or "shorts" or "live")
            {
                candidate = segments[1];
            }
        }

        if (candidate is null || !VideoIdRegex().IsMatch(candidate))
        {
            return false;
        }

        videoId = candidate;
        return true;
    }

    public static Uri CreatePrivacyEnhancedEmbedUri(string videoId)
    {
        if (!TryParseVideoId(videoId, out var normalized))
        {
            throw new ArgumentException("A valid YouTube video ID is required.", nameof(videoId));
        }

        return new($"https://www.youtube-nocookie.com/embed/{normalized}?enablejsapi=1&controls=1&playsinline=1");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0])] = parts.Length == 2
                ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                : string.Empty;
        }

        return result;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();
}

public static class MomentVqaRequestValidator
{
    public static void Validate(MomentVqaRequest request, MomentEvidenceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= MomentEvidenceLimits.Default;
        limits.Validate();
        if (request.SessionId == Guid.Empty || request.RequestId == Guid.Empty)
        {
            throw new InvalidOperationException("A viewer session and request ID are required.");
        }

        if (request.CurrentTimestampMs < 0 || string.IsNullOrWhiteSpace(request.Question))
        {
            throw new InvalidOperationException("A nonnegative timestamp and question are required.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceReference))
        {
            throw new InvalidOperationException("A non-sensitive source reference is required.");
        }

        if (request.SourceType == InteractiveVideoSourceType.LocalFile
            && (Path.IsPathRooted(request.SourceReference)
                || request.SourceReference.Contains('\\')))
        {
            throw new InvalidOperationException("Local paths must not be included in provider requests.");
        }

        var images = request.Evidence.Where(item => item.Kind == MomentEvidenceKind.EphemeralImage).ToArray();
        if (images.Length > limits.MaximumImageCount)
        {
            throw new InvalidOperationException("The request contains too many ephemeral images.");
        }

        if (images.Length > 1
            && images.Max(item => item.EndTimestampMs) - images.Min(item => item.StartTimestampMs)
            > Math.Max(limits.MaximumTemporalSpanMs, request.QuestionIntent == MomentQuestionIntent.RecentContext
                ? limits.MaximumRecentContextMs
                : limits.MaximumTemporalSpanMs))
        {
            throw new InvalidOperationException("The request evidence temporal span is too large.");
        }

        if (images.Any(item => item.PixelWidth > limits.MaximumImageDimension
                               || item.PixelHeight > limits.MaximumImageDimension
                               || !item.Ephemeral
                               || string.IsNullOrWhiteSpace(item.PrivateEphemeralPath)))
        {
            throw new InvalidOperationException("Ephemeral image evidence violates its size or lifecycle contract.");
        }
    }
}
