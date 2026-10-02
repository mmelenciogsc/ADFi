using System.Security.Cryptography;
using System.Text.Json;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record CreatorVisualUnderstandingConfiguration(
    string QwenModel,
    string FaceDetectorModelPath,
    string FaceRecognizerModelPath,
    string TesseractPath,
    string VisualTextPromptPath,
    string VisualAnalystPromptPath);

public sealed record VisualUnderstandingManifest(
    Guid ProjectId,
    string SourceSha256,
    IReadOnlyList<VisualSceneAnalysis> Scenes,
    DateTimeOffset CompletedAtUtc);

public sealed record CreatorVisualUnderstandingResult(
    FaceContinuityManifest FaceContinuity,
    VisualTextAnalysisManifest VisualText,
    VisualUnderstandingManifest SceneAnalysis);

public sealed record CreatorVisualUnderstandingProgress(
    AdficationPhase Phase,
    int CompletedUnits,
    int TotalUnits,
    string Announcement);

/// <summary>
/// Composes Phases 3-5 without keeping face models, Qwen and OCR work resident together.
/// Each final manifest is hashed before its phase checkpoint is committed.
/// </summary>
public sealed class CreatorVisualUnderstandingWorkflow(
    IPipelineCheckpointStore checkpointStore,
    TimeProvider timeProvider,
    Func<IFaceContinuityAnalyzer> faceAnalyzerFactory,
    Func<ILocalModelRuntime> localModelRuntimeFactory,
    IOcrProcessRunner ocrProcessRunner)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<CreatorVisualUnderstandingResult> RunAsync(
        Guid projectId,
        string projectDirectory,
        CreatorVisualUnderstandingConfiguration configuration,
        IProgress<CreatorVisualUnderstandingProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration(configuration);
        var root = Path.GetFullPath(projectDirectory);
        var source = await LoadAsync<SourceIntakeResult>(
            Path.Combine(root, "manifests", "source-validated.json"),
            cancellationToken).ConfigureAwait(false);
        var audio = await LoadAsync<AudioAnalysisCompletionManifest>(
            Path.Combine(root, "manifests", "audio-analysis-complete.json"),
            cancellationToken).ConfigureAwait(false);
        var visual = await LoadAsync<VisualIndexManifest>(
            Path.Combine(root, "manifests", "visual-index.json"),
            cancellationToken).ConfigureAwait(false);
        ValidateInputs(projectId, source, audio, visual);

        var pipelineProgress = new InlineProgress<PipelineProgress>(item => progress?.Report(new(
            item.Phase,
            item.Status == CheckpointStatus.Complete ? 1 : 0,
            1,
            item.Announcement)));
        var coordinator = new SequentialPipelineCoordinator(
            [
                new FacePhase(
                    source,
                    visual,
                    root,
                    faceAnalyzerFactory,
                    timeProvider,
                    progress),
                new TextPhase(
                    source,
                    visual,
                    root,
                    configuration,
                    localModelRuntimeFactory,
                    ocrProcessRunner,
                    timeProvider,
                    progress),
                new SceneAnalysisPhase(
                    source,
                    audio,
                    visual,
                    root,
                    configuration,
                    localModelRuntimeFactory,
                    timeProvider,
                    progress),
            ],
            checkpointStore,
            timeProvider,
            pipelineProgress);
        await coordinator.RunAsync(projectId, cancellationToken).ConfigureAwait(false);

        return new(
            await LoadAsync<FaceContinuityManifest>(
                Path.Combine(root, "manifests", "face-continuity.json"),
                cancellationToken).ConfigureAwait(false),
            await LoadAsync<VisualTextAnalysisManifest>(
                Path.Combine(root, "manifests", "visual-text-analysis.json"),
                cancellationToken).ConfigureAwait(false),
            await LoadAsync<VisualUnderstandingManifest>(
                Path.Combine(root, "manifests", "visual-analysis-complete.json"),
                cancellationToken).ConfigureAwait(false));
    }

    private static void ValidateConfiguration(CreatorVisualUnderstandingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.QwenModel);
        foreach (var path in new[]
                 {
                     configuration.FaceDetectorModelPath,
                     configuration.FaceRecognizerModelPath,
                     configuration.VisualTextPromptPath,
                     configuration.VisualAnalystPromptPath,
                 })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("A required visual-analysis model or prompt is missing.", path);
            }
        }
    }

    private static void ValidateInputs(
        Guid projectId,
        SourceIntakeResult source,
        AudioAnalysisCompletionManifest audio,
        VisualIndexManifest visual)
    {
        if (source.Checkpoint.ProjectId != projectId
            || audio.ProjectId != projectId
            || visual.ProjectId != projectId
            || !string.Equals(source.Source.SourceSha256, audio.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(source.Source.SourceSha256, visual.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Visual-understanding inputs do not share one immutable project source.");
        }
    }

    private sealed class FacePhase(
        SourceIntakeResult source,
        VisualIndexManifest visual,
        string root,
        Func<IFaceContinuityAnalyzer> analyzerFactory,
        TimeProvider timeProvider,
        IProgress<CreatorVisualUnderstandingProgress>? progress) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => AdficationPhase.RecurringPersonContinuityAndCharacterGraph;

        public Task<bool> VerifyCheckpointAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken) =>
            VerifyAsync(checkpoint, root, "manifests/face-continuity.json", cancellationToken);

        public async Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            var faceProgress = new InlineProgress<(int Completed, int Total, string Announcement)>(item =>
                progress?.Report(new(Phase, item.Completed, item.Total, item.Announcement)));
            await new FaceContinuityIndexService(analyzerFactory, timeProvider, faceProgress).BuildAsync(
                projectId,
                source.Source.SourceSha256,
                visual,
                root,
                cancellationToken).ConfigureAwait(false);
            return await CompleteAsync(projectId, Phase, root, "manifests/face-continuity.json", timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class TextPhase(
        SourceIntakeResult source,
        VisualIndexManifest visual,
        string root,
        CreatorVisualUnderstandingConfiguration configuration,
        Func<ILocalModelRuntime> runtimeFactory,
        IOcrProcessRunner ocrProcessRunner,
        TimeProvider timeProvider,
        IProgress<CreatorVisualUnderstandingProgress>? progress) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => AdficationPhase.OcrAndVisualTextInterpretation;

        public async Task<bool> VerifyCheckpointAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            const string relative = "manifests/visual-text-analysis.json";
            if (!await VerifyAsync(checkpoint, root, relative, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                var manifest = await LoadAsync<VisualTextAnalysisManifest>(
                    Path.Combine(root, relative), cancellationToken).ConfigureAwait(false);
                var prompt = await PromptResourceLoader.LoadAsync(
                    configuration.VisualTextPromptPath, cancellationToken).ConfigureAwait(false);
                var expectedSceneCount = visual.Scenes.Count(scene => scene.Keyframes.Any(frame => frame.PotentialText));
                return manifest.ProjectId == source.Checkpoint.ProjectId
                       && string.Equals(manifest.SourceSha256, source.Source.SourceSha256, StringComparison.OrdinalIgnoreCase)
                       && manifest.Scenes.Count == expectedSceneCount
                       && manifest.PromptSha256 == prompt.Sha256
                       && manifest.ModelName == configuration.QwenModel;
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                return false;
            }
        }

        public async Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            await using var runtime = runtimeFactory();
            var textProgress = new InlineProgress<(int Completed, int Total, string Announcement)>(item =>
                progress?.Report(new(Phase, item.Completed, item.Total, item.Announcement)));
            await new VisualTextAnalysisCoordinator(
                    runtime,
                    configuration.QwenModel,
                    configuration.VisualTextPromptPath,
                    new TesseractOcrService(configuration.TesseractPath, ocrProcessRunner),
                    timeProvider,
                    textProgress)
                .AnalyzeAsync(
                    projectId,
                    source.Source.SourceSha256,
                    visual,
                    root,
                    cancellationToken).ConfigureAwait(false);
            return await CompleteAsync(projectId, Phase, root, "manifests/visual-text-analysis.json", timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class SceneAnalysisPhase(
        SourceIntakeResult source,
        AudioAnalysisCompletionManifest audio,
        VisualIndexManifest visual,
        string root,
        CreatorVisualUnderstandingConfiguration configuration,
        Func<ILocalModelRuntime> runtimeFactory,
        TimeProvider timeProvider,
        IProgress<CreatorVisualUnderstandingProgress>? progress) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => AdficationPhase.VlmSceneAnalytics;

        public async Task<bool> VerifyCheckpointAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            const string relative = "manifests/visual-analysis-complete.json";
            if (!await VerifyAsync(checkpoint, root, relative, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                var manifest = await LoadAsync<VisualUnderstandingManifest>(
                    Path.Combine(root, relative), cancellationToken).ConfigureAwait(false);
                var prompt = await PromptResourceLoader.LoadAsync(
                    configuration.VisualAnalystPromptPath, cancellationToken).ConfigureAwait(false);
                var packages = await BuildPackagesAsync(cancellationToken).ConfigureAwait(false);
                var expected = packages.ToDictionary(
                    item => item.Scene.SceneId,
                    VisualAnalysisCoordinator.ComputeInputFingerprint,
                    StringComparer.Ordinal);
                return manifest.ProjectId == source.Checkpoint.ProjectId
                       && string.Equals(manifest.SourceSha256, source.Source.SourceSha256, StringComparison.OrdinalIgnoreCase)
                       && manifest.Scenes.Count == packages.Length
                       && manifest.Scenes.All(scene =>
                           expected.TryGetValue(scene.SceneId, out var fingerprint)
                           && scene.InputFingerprint == fingerprint
                           && scene.Provenance.PromptId == prompt.Id
                           && scene.Provenance.PromptVersion == prompt.Version.ToString()
                           && scene.Provenance.PromptSha256 == prompt.Sha256
                           && scene.Provenance.ModelName == configuration.QwenModel);
            }
            catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
            {
                return false;
            }
        }

        public async Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            await using var runtime = runtimeFactory();
            var sceneProgress = new InlineProgress<VisualAnalysisProgress>(item => progress?.Report(new(
                Phase,
                item.CompletedScenes,
                item.TotalScenes,
                item.Announcement)));
            var packages = await BuildPackagesAsync(cancellationToken).ConfigureAwait(false);
            var analyses = await new VisualAnalysisCoordinator(
                    runtime,
                    configuration.QwenModel,
                    configuration.VisualAnalystPromptPath,
                    timeProvider,
                    sceneProgress)
                .AnalyzeAsync(projectId, root, packages, cancellationToken).ConfigureAwait(false);
            var manifest = new VisualUnderstandingManifest(
                projectId,
                source.Source.SourceSha256,
                analyses,
                timeProvider.GetUtcNow());
            await WriteAsync(
                Path.Combine(root, "manifests", "visual-analysis-complete.json"),
                manifest,
                cancellationToken).ConfigureAwait(false);
            return await CompleteAsync(projectId, Phase, root, "manifests/visual-analysis-complete.json", timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<VisualAnalysisPackage[]> BuildPackagesAsync(CancellationToken cancellationToken)
        {
            var faces = await LoadAsync<FaceContinuityManifest>(
                Path.Combine(root, "manifests", "face-continuity.json"),
                cancellationToken).ConfigureAwait(false);
            var text = await LoadAsync<VisualTextAnalysisManifest>(
                Path.Combine(root, "manifests", "visual-text-analysis.json"),
                cancellationToken).ConfigureAwait(false);
            return visual.Scenes.Select(scene => new VisualAnalysisPackage(
                scene,
                scene.Keyframes.ToDictionary(
                    frame => frame.EvidenceId,
                    frame => Path.Combine(root, frame.RelativeImagePath.Replace('/', Path.DirectorySeparatorChar)),
                    StringComparer.Ordinal),
                audio.Transcript
                    .Where(segment => scene.TranscriptSegmentIds.Contains(segment.SegmentId, StringComparer.Ordinal))
                    .ToDictionary(segment => segment.SegmentId, segment => segment.Text, StringComparer.Ordinal),
                faces.Entities,
                text.Scenes.FirstOrDefault(item => item.SceneId == scene.SceneId)?.ReconciledReadings ?? [],
                faces.Observations.Where(item => item.SceneId == scene.SceneId).ToArray()))
                .ToArray();
        }
    }

    private static async Task<PipelineCheckpoint> CompleteAsync(
        Guid projectId,
        AdficationPhase phase,
        string root,
        string relative,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return new(
            projectId,
            phase,
            CheckpointStatus.Complete,
            timeProvider.GetUtcNow(),
            relative,
            await HashAsync(path, cancellationToken).ConfigureAwait(false),
            null);
    }

    private static async Task<bool> VerifyAsync(
        PipelineCheckpoint checkpoint,
        string root,
        string relative,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(checkpoint.ManifestRelativePath, relative, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(checkpoint.OutputSha256))
        {
            return false;
        }

        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path)
               && string.Equals(
                   checkpoint.OutputSha256,
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
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
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
