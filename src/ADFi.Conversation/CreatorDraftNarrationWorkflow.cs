using System.Security.Cryptography;
using System.Text.Json;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record CreatorDraftNarrationConfiguration(
    string QwenModel,
    string WriterPromptPath,
    string VoiceId,
    TimeSpan MinimumNarrationWindow,
    TimeSpan BreathingRoom);

public sealed record AdOpportunityManifest(
    Guid ProjectId,
    string SourceSha256,
    string AudioAnalysisSha256,
    string VisualIndexSha256,
    string VisualAnalysisSha256,
    IReadOnlyList<VisualFact> VerifiedFacts,
    IReadOnlyList<ProposedAdInterval> Opportunities,
    DateTimeOffset CompletedAtUtc);

public sealed record NarrationSegmentManifest(
    string VoiceId,
    AdSegment Segment,
    NarrationFitResult Fit,
    int SynthesisAttempts,
    bool RequiresUserReview,
    string AudioSha256);

public sealed record NarrationCompletionManifest(
    Guid ProjectId,
    string SourceSha256,
    string DraftManifestSha256,
    string VoiceId,
    IReadOnlyList<NarrationSegmentManifest> Segments,
    DateTimeOffset CompletedAtUtc);

public sealed record CreatorDraftNarrationResult(
    AdOpportunityManifest Opportunities,
    AdDraftCompletionManifest Draft,
    NarrationCompletionManifest Narration);

public sealed record CreatorDraftNarrationProgress(
    AdficationPhase Phase,
    int CompletedUnits,
    int TotalUnits,
    string Announcement);

/// <summary>
/// Composes Phases 6-8 from verified project evidence. Draft text and narration are
/// persisted per segment, and every completed phase is protected by a manifest hash.
/// Piper synthesis and Qwen shortening run sequentially and never overlap.
/// </summary>
public sealed class CreatorDraftNarrationWorkflow(
    IPipelineCheckpointStore checkpointStore,
    TimeProvider timeProvider,
    Func<ILocalModelRuntime> localModelRuntimeFactory,
    Func<string, INarrationSynthesizer> narrationSynthesizerFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<CreatorDraftNarrationResult> RunAsync(
        Guid projectId,
        string projectDirectory,
        CreatorDraftNarrationConfiguration configuration,
        IProgress<CreatorDraftNarrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration(configuration);
        var root = Path.GetFullPath(projectDirectory);
        var source = await LoadAsync<SourceIntakeResult>(
            Path.Combine(root, "manifests", "source-validated.json"), cancellationToken).ConfigureAwait(false);
        var audio = await LoadAsync<AudioAnalysisCompletionManifest>(
            Path.Combine(root, "manifests", "audio-analysis-complete.json"), cancellationToken).ConfigureAwait(false);
        var visual = await LoadAsync<VisualIndexManifest>(
            Path.Combine(root, "manifests", "visual-index.json"), cancellationToken).ConfigureAwait(false);
        var faces = await LoadAsync<FaceContinuityManifest>(
            Path.Combine(root, "manifests", "face-continuity.json"), cancellationToken).ConfigureAwait(false);
        var analysis = await LoadAsync<VisualUnderstandingManifest>(
            Path.Combine(root, "manifests", "visual-analysis-complete.json"), cancellationToken).ConfigureAwait(false);
        ValidateInputs(projectId, source, audio, visual, faces, analysis);
        var audioHash = await HashAsync(Path.Combine(root, "manifests", "audio-analysis-complete.json"), cancellationToken)
            .ConfigureAwait(false);
        var visualHash = await HashAsync(Path.Combine(root, "manifests", "visual-index.json"), cancellationToken)
            .ConfigureAwait(false);
        var faceHash = await HashAsync(Path.Combine(root, "manifests", "face-continuity.json"), cancellationToken)
            .ConfigureAwait(false);
        var analysisHash = await HashAsync(Path.Combine(root, "manifests", "visual-analysis-complete.json"), cancellationToken)
            .ConfigureAwait(false);

        var phaseProgress = new InlineProgress<PipelineProgress>(item => progress?.Report(new(
            item.Phase,
            item.Status == CheckpointStatus.Complete ? 1 : 0,
            1,
            item.Announcement)));
        await new SequentialPipelineCoordinator(
            [
                new OpportunityPhase(source, audio, visual, analysis, audioHash, visualHash, analysisHash,
                    root, configuration, timeProvider, progress),
                new DraftPhase(source, audio, faces, faceHash, root, configuration,
                    localModelRuntimeFactory, timeProvider, progress),
                new NarrationPhase(source, root, configuration, localModelRuntimeFactory,
                    narrationSynthesizerFactory, timeProvider, progress),
            ],
            checkpointStore,
            timeProvider,
            phaseProgress).RunAsync(projectId, cancellationToken).ConfigureAwait(false);

        return new(
            await LoadAsync<AdOpportunityManifest>(Path.Combine(root, "manifests", "ad-opportunities.json"), cancellationToken)
                .ConfigureAwait(false),
            await LoadAsync<AdDraftCompletionManifest>(Path.Combine(root, "manifests", "ad-draft-complete.json"), cancellationToken)
                .ConfigureAwait(false),
            await LoadAsync<NarrationCompletionManifest>(Path.Combine(root, "manifests", "narration-complete.json"), cancellationToken)
                .ConfigureAwait(false));
    }

    private static void ValidateConfiguration(CreatorDraftNarrationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.QwenModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.VoiceId);
        _ = PiperVoiceCatalog.Get(configuration.VoiceId);
        if (!File.Exists(configuration.WriterPromptPath))
        {
            throw new FileNotFoundException("The AD writer prompt is missing.", configuration.WriterPromptPath);
        }

        if (configuration.MinimumNarrationWindow <= TimeSpan.Zero
            || configuration.BreathingRoom < TimeSpan.Zero
            || configuration.BreathingRoom >= configuration.MinimumNarrationWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    private static void ValidateInputs(
        Guid projectId,
        SourceIntakeResult source,
        AudioAnalysisCompletionManifest audio,
        VisualIndexManifest visual,
        FaceContinuityManifest faces,
        VisualUnderstandingManifest analysis)
    {
        var sourceHash = source.Source.SourceSha256;
        if (source.Checkpoint.ProjectId != projectId
            || audio.ProjectId != projectId
            || visual.ProjectId != projectId
            || faces.ProjectId != projectId
            || analysis.ProjectId != projectId
            || !new[] { audio.SourceSha256, visual.SourceSha256, faces.SourceSha256, analysis.SourceSha256 }
                .All(hash => string.Equals(hash, sourceHash, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Draft inputs do not share one immutable project source.");
        }
    }

    private sealed class OpportunityPhase(
        SourceIntakeResult source,
        AudioAnalysisCompletionManifest audio,
        VisualIndexManifest visual,
        VisualUnderstandingManifest analysis,
        string audioAnalysisSha256,
        string visualIndexSha256,
        string visualAnalysisSha256,
        string root,
        CreatorDraftNarrationConfiguration configuration,
        TimeProvider timeProvider,
        IProgress<CreatorDraftNarrationProgress>? progress) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => AdficationPhase.AdNecessityAndNarrationWindows;

        public async Task<bool> VerifyCheckpointAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            const string relative = "manifests/ad-opportunities.json";
            if (!await VerifyAsync(checkpoint, root, relative, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                var manifest = await LoadAsync<AdOpportunityManifest>(
                    Path.Combine(root, "manifests", "ad-opportunities.json"), cancellationToken).ConfigureAwait(false);
                return manifest.ProjectId == source.Checkpoint.ProjectId
                       && string.Equals(manifest.SourceSha256, source.Source.SourceSha256, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(manifest.AudioAnalysisSha256, audioAnalysisSha256, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(manifest.VisualIndexSha256, visualIndexSha256, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(manifest.VisualAnalysisSha256, visualAnalysisSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                return false;
            }
        }

        public async Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            progress?.Report(new(Phase, 0, analysis.Scenes.Count, "Selecting necessary descriptions and quiet narration windows."));
            var sceneLookup = visual.Scenes.ToDictionary(scene => scene.SceneId, StringComparer.Ordinal);
            var facts = analysis.Scenes
                .OrderBy(scene => sceneLookup[scene.SceneId].SourceInterval.Start)
                .SelectMany(scene => ToFacts(scene, sceneLookup[scene.SceneId]))
                .ToArray();
            var opportunities = AdNecessityPolicy.Propose(
                visual.Scenes,
                facts,
                audio.NarrationWindows,
                configuration.MinimumNarrationWindow);
            var manifest = new AdOpportunityManifest(
                projectId,
                source.Source.SourceSha256,
                audioAnalysisSha256,
                visualIndexSha256,
                visualAnalysisSha256,
                facts,
                opportunities,
                timeProvider.GetUtcNow());
            await WriteAsync(Path.Combine(root, "manifests", "ad-opportunities.json"), manifest, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new(Phase, analysis.Scenes.Count, analysis.Scenes.Count,
                $"Selected {opportunities.Count} grounded narration opportunities."));
            return await CompleteAsync(projectId, Phase, root, "manifests/ad-opportunities.json", timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        private static IEnumerable<VisualFact> ToFacts(VisualSceneAnalysis analysis, VisualScene scene)
        {
            var frameTimes = scene.Keyframes.ToDictionary(frame => frame.EvidenceId, frame => frame.SourceTime, StringComparer.Ordinal);
            foreach (var observation in analysis.Observations)
            {
                if (observation.EvidenceIds.Count == 0
                    || observation.EvidenceIds.Any(id => !frameTimes.ContainsKey(id)))
                {
                    throw new InvalidDataException($"Scene {scene.SceneId} contains unsupported visual evidence.");
                }

                yield return new(
                    observation.ObservationId,
                    scene.SceneId,
                    observation.ObjectiveDescription,
                    observation.Importance,
                    observation.Certainty,
                    observation.AlreadyConveyedBySourceAudio,
                    observation.EvidenceIds.Max(id => frameTimes[id]),
                    null,
                    observation.EvidenceIds);
            }
        }
    }

    private sealed class DraftPhase(
        SourceIntakeResult source,
        AudioAnalysisCompletionManifest audio,
        FaceContinuityManifest faces,
        string faceContinuitySha256,
        string root,
        CreatorDraftNarrationConfiguration configuration,
        Func<ILocalModelRuntime> runtimeFactory,
        TimeProvider timeProvider,
        IProgress<CreatorDraftNarrationProgress>? progress) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => AdficationPhase.DraftGeneration;

        public async Task<bool> VerifyCheckpointAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            const string relative = "manifests/ad-draft-complete.json";
            if (!await VerifyAsync(checkpoint, root, relative, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                var manifest = await LoadAsync<AdDraftCompletionManifest>(
                    Path.Combine(root, "manifests", "ad-draft-complete.json"), cancellationToken).ConfigureAwait(false);
                var prompt = await PromptResourceLoader.LoadAsync(configuration.WriterPromptPath, cancellationToken)
                    .ConfigureAwait(false);
                return manifest.ProjectId == source.Checkpoint.ProjectId
                       && string.Equals(manifest.SourceSha256, source.Source.SourceSha256, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(
                           manifest.OpportunityManifestSha256,
                           await HashAsync(Path.Combine(root, "manifests", "ad-opportunities.json"), cancellationToken)
                               .ConfigureAwait(false),
                           StringComparison.OrdinalIgnoreCase)
                       && string.Equals(manifest.FaceContinuitySha256, faceContinuitySha256, StringComparison.OrdinalIgnoreCase)
                       && manifest.Segments.All(segment =>
                           segment.PromptId == prompt.Id
                           && segment.PromptVersion == prompt.Version.ToString()
                           && segment.PromptSha256 == prompt.Sha256);
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
            {
                return false;
            }
        }

        public async Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            var opportunities = await LoadAsync<AdOpportunityManifest>(
                Path.Combine(root, "manifests", "ad-opportunities.json"), cancellationToken).ConfigureAwait(false);
            ValidateProjectSource(projectId, source.Source.SourceSha256, opportunities.ProjectId, opportunities.SourceSha256);
            var opportunityHash = await HashAsync(
                Path.Combine(root, "manifests", "ad-opportunities.json"), cancellationToken).ConfigureAwait(false);
            var transcript = audio.Transcript.ToDictionary(segment => segment.SegmentId, segment => segment.Text, StringComparer.Ordinal);
            var packages = opportunities.Opportunities.Select(opportunity => new AdDraftPackage(
                opportunity,
                opportunities.VerifiedFacts.Where(fact => fact.SceneId == opportunity.SceneId).ToArray(),
                opportunity.NearbyTranscriptSegmentIds
                    .Where(transcript.ContainsKey)
                    .Select(id => transcript[id])
                    .ToArray(),
                faces.Entities)).ToArray();
            progress?.Report(new(Phase, 0, packages.Length, $"Drafting description 0 of {packages.Length}."));
            await using var runtime = runtimeFactory();
            var draftProgress = new InlineProgress<(int Completed, int Total, string Announcement)>(item =>
                progress?.Report(new(Phase, item.Completed, item.Total, item.Announcement)));
            var segments = await new AdDraftCoordinator(
                    runtime,
                    configuration.QwenModel,
                    configuration.WriterPromptPath,
                    draftProgress)
                .DraftAsync(projectId, root, packages, cancellationToken).ConfigureAwait(false);
            await WriteAsync(
                Path.Combine(root, "manifests", "ad-draft-complete.json"),
                new AdDraftCompletionManifest(
                    projectId,
                    segments,
                    source.Source.SourceSha256,
                    opportunityHash,
                    faceContinuitySha256),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new(Phase, segments.Count, packages.Length,
                $"Drafted {segments.Count} evidence-grounded descriptions."));
            return await CompleteAsync(projectId, Phase, root, "manifests/ad-draft-complete.json", timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class NarrationPhase(
        SourceIntakeResult source,
        string root,
        CreatorDraftNarrationConfiguration configuration,
        Func<ILocalModelRuntime> runtimeFactory,
        Func<string, INarrationSynthesizer> synthesizerFactory,
        TimeProvider timeProvider,
        IProgress<CreatorDraftNarrationProgress>? progress) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => AdficationPhase.PiperNarrationAndTimingValidation;

        public async Task<bool> VerifyCheckpointAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            const string relative = "manifests/narration-complete.json";
            if (!await VerifyAsync(checkpoint, root, relative, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                var manifest = await LoadAsync<NarrationCompletionManifest>(
                    Path.Combine(root, "manifests", "narration-complete.json"), cancellationToken).ConfigureAwait(false);
                var draft = await LoadAsync<AdDraftCompletionManifest>(
                    Path.Combine(root, "manifests", "ad-draft-complete.json"), cancellationToken).ConfigureAwait(false);
                if (manifest.ProjectId != source.Checkpoint.ProjectId
                    || !string.Equals(manifest.SourceSha256, source.Source.SourceSha256, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        manifest.DraftManifestSha256,
                        await HashAsync(Path.Combine(root, "manifests", "ad-draft-complete.json"), cancellationToken)
                            .ConfigureAwait(false),
                        StringComparison.OrdinalIgnoreCase)
                    || manifest.VoiceId != configuration.VoiceId
                    || manifest.Segments.Count != draft.Segments.Count)
                {
                    return false;
                }

                for (var index = 0; index < draft.Segments.Count; index++)
                {
                    if (!await IsValidNarrationSegmentAsync(
                            root, manifest.Segments[index], draft.Segments[index], configuration.VoiceId, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidDataException or ArgumentException)
            {
                return false;
            }
        }

        public async Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            var draft = await LoadAsync<AdDraftCompletionManifest>(
                Path.Combine(root, "manifests", "ad-draft-complete.json"), cancellationToken).ConfigureAwait(false);
            if (draft.ProjectId != projectId)
            {
                throw new InvalidDataException("The AD draft belongs to another project.");
            }

            var draftHash = await HashAsync(
                Path.Combine(root, "manifests", "ad-draft-complete.json"), cancellationToken).ConfigureAwait(false);

            await using var runtime = runtimeFactory();
            var fitter = new NarrationAutoFitCoordinator(
                synthesizerFactory(root), runtime, configuration.QwenModel, configuration.WriterPromptPath);
            var completed = new List<NarrationSegmentManifest>(draft.Segments.Count);
            for (var index = 0; index < draft.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = draft.Segments[index];
                var existing = await TryLoadNarrationSegmentAsync(
                    root, segment, configuration.VoiceId, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    var fitted = await fitter.FitAsync(
                        segment,
                        configuration.VoiceId,
                        configuration.BreathingRoom,
                        cancellationToken).ConfigureAwait(false);
                    var audioPath = fitted.Segment.NarrationAssetRelativePath
                                    ?? throw new InvalidDataException("Piper returned no narration asset.");
                    var relativeAudio = Path.GetRelativePath(root, Path.GetFullPath(audioPath)).Replace('\\', '/');
                    if (relativeAudio.StartsWith("../", StringComparison.Ordinal)
                        || Path.IsPathRooted(relativeAudio))
                    {
                        throw new InvalidDataException("Piper narration was written outside the project.");
                    }

                    var updated = fitted.Segment with { NarrationAssetRelativePath = relativeAudio };
                    existing = new(
                        configuration.VoiceId,
                        updated,
                        fitted.Fit,
                        fitted.SynthesisAttempts,
                        fitted.RequiresUserReview,
                        await HashAsync(Path.Combine(root, relativeAudio.Replace('/', Path.DirectorySeparatorChar)), cancellationToken)
                            .ConfigureAwait(false));
                    await WriteAsync(NarrationSegmentPath(root, segment.SegmentId), existing, cancellationToken)
                        .ConfigureAwait(false);
                }

                completed.Add(existing);
                progress?.Report(new(Phase, index + 1, draft.Segments.Count,
                    $"Narration {index + 1} of {draft.Segments.Count} complete."));
            }

            var manifest = new NarrationCompletionManifest(
                projectId,
                source.Source.SourceSha256,
                draftHash,
                configuration.VoiceId,
                completed,
                timeProvider.GetUtcNow());
            await WriteAsync(Path.Combine(root, "manifests", "narration-complete.json"), manifest, cancellationToken)
                .ConfigureAwait(false);
            return await CompleteAsync(projectId, Phase, root, "manifests/narration-complete.json", timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<NarrationSegmentManifest?> TryLoadNarrationSegmentAsync(
        string root,
        AdSegment draft,
        string voiceId,
        CancellationToken cancellationToken)
    {
        var path = NarrationSegmentPath(root, draft.SegmentId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var item = await LoadAsync<NarrationSegmentManifest>(path, cancellationToken).ConfigureAwait(false);
            return await IsValidNarrationSegmentAsync(root, item, draft, voiceId, cancellationToken)
                .ConfigureAwait(false)
                ? item
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> IsValidNarrationSegmentAsync(
        string root,
        NarrationSegmentManifest item,
        AdSegment draft,
        string voiceId,
        CancellationToken cancellationToken)
    {
        var audioRelative = item.Segment.NarrationAssetRelativePath;
        if (item.VoiceId != voiceId
            || item.Segment.SegmentId != draft.SegmentId
            || item.Segment.SceneId != draft.SceneId
            || item.Segment.Window != draft.Window
            || item.Segment.Text != draft.Text
            || !item.Segment.EvidenceIds.SequenceEqual(draft.EvidenceIds, StringComparer.Ordinal)
            || item.Segment.PromptId != draft.PromptId
            || item.Segment.PromptVersion != draft.PromptVersion
            || item.Segment.PromptSha256 != draft.PromptSha256
            || item.Segment.InputFingerprint != draft.InputFingerprint
            || item.Segment.UserApproved != draft.UserApproved
            || string.IsNullOrWhiteSpace(audioRelative))
        {
            return false;
        }

        var audioPath = Path.GetFullPath(Path.Combine(root, audioRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!audioPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(audioPath)
            || !string.Equals(item.AudioSha256, await HashAsync(audioPath, cancellationToken).ConfigureAwait(false),
                StringComparison.OrdinalIgnoreCase)
            || item.Segment.MeasuredNarrationDuration is not { } measured
            || item.Fit.Available <= TimeSpan.Zero
            || item.Fit.Available > item.Segment.Window.Duration
            || item.SynthesisAttempts != item.Fit.AttemptNumber
            || item.RequiresUserReview != item.Fit.RequiresUserReview)
        {
            return false;
        }

        var expectedFit = NarrationTimingPolicy.Evaluate(
            measured,
            item.Segment.Window.Duration,
            item.Segment.Window.Duration - item.Fit.Available,
            item.Fit.AttemptNumber);
        return expectedFit == item.Fit;
    }

    private static string NarrationSegmentPath(string root, string segmentId)
    {
        var safe = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(segmentId))).ToLowerInvariant();
        return Path.Combine(root, "narration", "segments", safe + ".json");
    }

    private static void ValidateProjectSource(
        Guid projectId,
        string sourceSha256,
        Guid manifestProjectId,
        string manifestSourceSha256)
    {
        if (projectId != manifestProjectId
            || !string.Equals(sourceSha256, manifestSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A downstream manifest does not belong to the selected immutable source.");
        }
    }

    private static async Task<PipelineCheckpoint> CompleteAsync(
        Guid projectId,
        AdficationPhase phase,
        string root,
        string relative,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        new(
            projectId,
            phase,
            CheckpointStatus.Complete,
            timeProvider.GetUtcNow(),
            relative,
            await HashAsync(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), cancellationToken)
                .ConfigureAwait(false),
            null);

    private static async Task<bool> VerifyAsync(
        PipelineCheckpoint checkpoint,
        string root,
        string expectedRelative,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, expectedRelative.Replace('/', Path.DirectorySeparatorChar));
        return checkpoint.ManifestRelativePath == expectedRelative
               && !string.IsNullOrWhiteSpace(checkpoint.OutputSha256)
               && File.Exists(path)
               && string.Equals(checkpoint.OutputSha256,
                   await HashAsync(path, cancellationToken).ConfigureAwait(false),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A required verified project manifest is missing.", path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Project manifest is empty: {Path.GetFileName(path)}");
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
