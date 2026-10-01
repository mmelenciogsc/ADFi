using System.Text.Json;
using ADFi.Core;
using ADFi.LocalAI;

namespace ADFi.Conversation;

public sealed class InteractiveVideoSessionCoordinator : IInteractiveVideoSession
{
    private readonly IPlaybackController playback;
    private readonly IVideoContextResolver contextResolver;
    private readonly IReadOnlyList<IVideoEvidenceProvider> evidenceProviders;
    private readonly Dictionary<VqaProviderKind, IVqaProvider> vqaProviders;
    private readonly IEphemeralEvidenceLifecycle evidenceLifecycle;
    private readonly TimeProvider timeProvider;
    private readonly MomentEvidenceLimits limits;
    private IReadOnlyList<MomentVqaEvidence> activeEvidence = [];
    private bool disposed;

    public InteractiveVideoSessionCoordinator(
        InteractiveVideoSource source,
        IPlaybackController playback,
        IVideoContextResolver contextResolver,
        IEnumerable<IVideoEvidenceProvider> evidenceProviders,
        IEnumerable<IVqaProvider> vqaProviders,
        IEphemeralEvidenceLifecycle evidenceLifecycle,
        TimeProvider timeProvider,
        MomentEvidenceLimits? limits = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        this.evidenceProviders = evidenceProviders?.ToArray() ?? throw new ArgumentNullException(nameof(evidenceProviders));
        this.vqaProviders = vqaProviders?.ToDictionary(provider => provider.Kind)
            ?? throw new ArgumentNullException(nameof(vqaProviders));
        this.evidenceLifecycle = evidenceLifecycle ?? throw new ArgumentNullException(nameof(evidenceLifecycle));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.limits = limits ?? MomentEvidenceLimits.Default;
        this.limits.Validate();
        SessionId = Guid.NewGuid();
    }

    public Guid SessionId { get; }

    public InteractiveVideoSource Source { get; }

    public PlaybackCheckpoint? ActiveCheckpoint { get; private set; }

    public async Task<PlaybackCheckpoint> PauseForQuestionAsync(
        string focusOrigin,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(focusOrigin);
        if (ActiveCheckpoint is not null)
        {
            return ActiveCheckpoint;
        }

        var before = await playback.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (before.State == InteractivePlaybackState.Unavailable)
        {
            throw new InvalidOperationException("The video player is unavailable.");
        }

        if (before.State == InteractivePlaybackState.Playing)
        {
            await playback.PauseAsync(cancellationToken).ConfigureAwait(false);
        }

        var paused = await playback.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (paused.State == InteractivePlaybackState.Playing)
        {
            throw new InvalidOperationException("ADFi could not confirm that playback paused.");
        }

        ActiveCheckpoint = new(
            SessionId,
            Source.SourceType,
            Source.SourceReference,
            Source.ProjectId,
            Source.PublicationId,
            Source.YouTubeVideoId,
            Math.Max(0, paused.CurrentTimestampMs),
            before.State,
            paused.PlaybackRate <= 0 ? 1m : paused.PlaybackRate,
            paused.SelectedCaptionsState,
            focusOrigin,
            Guid.NewGuid(),
            timeProvider.GetUtcNow());
        evidenceLifecycle.GetSessionDirectory(SessionId);
        return ActiveCheckpoint;
    }

    public async Task<MomentVqaResult> AskAsync(
        string question,
        VqaProviderKind provider,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var checkpoint = ActiveCheckpoint
            ?? throw new InvalidOperationException("Pause the video with Ask about this moment first.");
        if (!vqaProviders.TryGetValue(provider, out var selectedProvider))
        {
            throw new InvalidOperationException("The selected VQA provider is not configured.");
        }

        var availability = await selectedProvider.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.CanSubmit)
        {
            throw new InvalidOperationException(availability.AccessibleStatus);
        }

        var requirement = MomentQuestionClassifier.Classify(question, limits);
        var resolved = await contextResolver.ResolveAsync(
            Source,
            checkpoint.CurrentTimestampMs,
            requirement,
            cancellationToken).ConfigureAwait(false);
        var evidence = resolved.ExistingEvidence.ToList();
        var captureState = Source.SourceType switch
        {
            InteractiveVideoSourceType.LocalFile => CaptureComplianceState.AllowedLocalFile,
            InteractiveVideoSourceType.AdfiPublication => CaptureComplianceState.ExistingAdfiEvidenceOnly,
            _ => CaptureComplianceState.YoutubeCaptureDisabled,
        };

        try
        {
            if (requirement.RequiresVisualEvidence && !resolved.ExistingEvidenceCanAnswer)
            {
                if (activeEvidence.Count > 0)
                {
                    evidence.AddRange(activeEvidence);
                    captureState = Source.SourceType == InteractiveVideoSourceType.YouTubeStream
                        ? CaptureComplianceState.YoutubeCaptureEnabledAfterApproval
                        : CaptureComplianceState.AllowedLocalFile;
                }
                else
                {
                    var evidenceProvider = evidenceProviders.FirstOrDefault(candidate => candidate.Supports(Source.SourceType));
                    if (evidenceProvider is not null)
                    {
                        var acquired = await evidenceProvider.AcquireAsync(
                            new(
                                SessionId,
                                checkpoint.VqaRequestId,
                                Source,
                                checkpoint.CurrentTimestampMs,
                                requirement,
                                true,
                                evidenceLifecycle.GetSessionDirectory(SessionId),
                                timeProvider.GetUtcNow()),
                            cancellationToken).ConfigureAwait(false);
                        activeEvidence = acquired;
                        evidence.AddRange(acquired);
                        captureState = acquired.Any(item => item.Basis == MomentEvidenceBasis.NewYoutubeEphemeralCapture)
                            ? CaptureComplianceState.YoutubeCaptureEnabledAfterApproval
                            : acquired.Count > 0
                                ? CaptureComplianceState.AllowedLocalFile
                                : CaptureComplianceState.YoutubeCaptureDisabled;
                    }
                    else if (Source.SourceType == InteractiveVideoSourceType.YouTubeStream)
                    {
                        captureState = CaptureComplianceState.YoutubeCaptureDisabled;
                    }
                    else
                    {
                        captureState = CaptureComplianceState.EvidenceUnavailable;
                    }
                }
            }

            var request = new MomentVqaRequest(
                SessionId,
                checkpoint.VqaRequestId,
                Source.SourceType,
                Source.SourceReference,
                checkpoint.CurrentTimestampMs,
                question.Trim(),
                requirement.Intent,
                evidence,
                resolved.Scene,
                resolved.EstablishedEntities,
                resolved.OcrContext,
                requirement.IncludeDialogueWhenAvailable ? resolved.DialogueContext : [],
                provider,
                EvidenceRetentionPolicy.RetainDuringActiveQuestionTransaction,
                captureState);
            MomentVqaRequestValidator.Validate(request, limits);
            return await selectedProvider.AskAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await evidenceLifecycle.DeleteRequestAsync(
                SessionId,
                checkpoint.VqaRequestId,
                CancellationToken.None).ConfigureAwait(false);
            activeEvidence = [];
            throw;
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var checkpoint = ActiveCheckpoint
            ?? throw new InvalidOperationException("There is no paused question checkpoint to resume.");
        await evidenceLifecycle.DeleteRequestAsync(
            SessionId,
            checkpoint.VqaRequestId,
            cancellationToken).ConfigureAwait(false);
        activeEvidence = [];
        await playback.SeekAsync(checkpoint.CurrentTimestampMs, cancellationToken).ConfigureAwait(false);
        await playback.PlayAsync(cancellationToken).ConfigureAwait(false);
        ActiveCheckpoint = null;
    }

    public async Task CloseQuestionAsync(bool resumePlayback, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (resumePlayback)
        {
            await ResumeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (ActiveCheckpoint is { } checkpoint)
        {
            await evidenceLifecycle.DeleteRequestAsync(
                SessionId,
                checkpoint.VqaRequestId,
                cancellationToken).ConfigureAwait(false);
        }

        activeEvidence = [];
        ActiveCheckpoint = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await evidenceLifecycle.DeleteSessionAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

public sealed class EmptyVideoContextResolver : IVideoContextResolver
{
    public Task<VideoContextResolution> ResolveAsync(
        InteractiveVideoSource source,
        long currentTimestampMs,
        MomentEvidenceRequirement requirement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new VideoContextResolution(null, [], [], [], [], false));
    }
}

public sealed class UnavailableVqaProvider(
    VqaProviderKind kind,
    VqaProviderAvailabilityState state,
    string accessibleStatus,
    Uri? manageUsageUri = null) : IVqaProvider
{
    public VqaProviderKind Kind => kind;

    public Task<VqaProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new VqaProviderAvailability(
            kind,
            state,
            false,
            accessibleStatus,
            manageUsageUri));
    }

    public Task<MomentVqaResult> AskAsync(MomentVqaRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(accessibleStatus);
}

public sealed class LocalMomentVqaProvider(
    ILocalModelRuntime runtime,
    string modelName,
    string promptPath,
    TimeProvider timeProvider) : IVqaProvider
{
    private const string ResultSchema =
        "{\"type\":\"object\",\"required\":[\"answer\",\"confidence\",\"uncertainties\",\"suggestedFollowup\"],\"properties\":{\"answer\":{\"type\":\"string\"},\"confidence\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"uncertainties\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"suggestedFollowup\":{\"type\":[\"string\",\"null\"]}},\"additionalProperties\":false}";

    public VqaProviderKind Kind => VqaProviderKind.LocalFirst;

    public Task<VqaProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new VqaProviderAvailability(
            Kind,
            VqaProviderAvailabilityState.LocalReady,
            true,
            "Local First is ready.",
            null));
    }

    public async Task<MomentVqaResult> AskAsync(
        MomentVqaRequest request,
        CancellationToken cancellationToken)
    {
        MomentVqaRequestValidator.Validate(request);
        var prompt = await PromptResourceLoader.LoadAsync(promptPath, cancellationToken).ConfigureAwait(false);
        var safeContext = new
        {
            request.SourceType,
            request.CurrentTimestampMs,
            request.Question,
            request.QuestionIntent,
            evidence = request.Evidence.Select(item => new
            {
                item.EvidenceId,
                item.Kind,
                item.Basis,
                item.StartTimestampMs,
                item.EndTimestampMs,
                item.Text,
                item.Confidence,
            }),
            request.AdfiSceneContext,
            request.EstablishedEntities,
            request.OcrContext,
            request.DialogueContext,
            request.CaptureComplianceState,
        };
        await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await runtime.GenerateAsync(
                modelName,
                new(
                    prompt.Content,
                    JsonSerializer.Serialize(safeContext),
                    request.Evidence
                        .Where(item => item.Kind == MomentEvidenceKind.EphemeralImage)
                        .Select(item => item.PrivateEphemeralPath)
                        .OfType<string>()
                        .ToArray(),
                    ResultSchema,
                    300),
                cancellationToken).ConfigureAwait(false);
            var answer = JsonSerializer.Deserialize<LocalMomentAnswer>(response.Content, JsonOptions)
                ?? throw new InvalidDataException("Local VQA returned an empty result.");
            if (string.IsNullOrWhiteSpace(answer.Answer))
            {
                throw new InvalidDataException("Local VQA returned no answer.");
            }

            return new(
                answer.Answer.Trim(),
                request.Evidence.Select(item => item.Basis).Distinct().ToArray(),
                Math.Clamp(answer.Confidence, 0m, 1m),
                answer.Uncertainties ?? [],
                string.IsNullOrWhiteSpace(answer.SuggestedFollowup) ? null : answer.SuggestedFollowup.Trim(),
                Kind,
                timeProvider.GetUtcNow());
        }
        finally
        {
            await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record LocalMomentAnswer(
        string Answer,
        decimal Confidence,
        IReadOnlyList<string>? Uncertainties,
        string? SuggestedFollowup);
}
