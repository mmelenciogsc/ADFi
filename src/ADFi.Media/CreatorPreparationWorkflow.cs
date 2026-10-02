using System.Security.Cryptography;
using System.Text.Json;
using ADFi.Core;

namespace ADFi.Media;

public sealed record CreatorPreparationConfiguration(
    string FfmpegPath,
    WhisperRuntimeConfiguration Whisper,
    string WhisperModel,
    int MaximumImageDimension,
    int MaximumFramesPerScene);

public sealed record AudioAnalysisCompletionManifest(
    Guid ProjectId,
    string SourceSha256,
    string WhisperModel,
    AudioPreparationManifest Audio,
    IReadOnlyList<TranscriptSegment> Transcript,
    IReadOnlyList<SourceAudioInterval> SourceAudioTimeline,
    IReadOnlyList<MediaInterval> NarrationWindows,
    DateTimeOffset CompletedAtUtc);

public sealed record CreatorPreparationResult(
    AudioAnalysisCompletionManifest AudioAnalysis,
    VisualIndexManifest VisualIndex);

public sealed record CreatorPreparationProgress(
    AdficationPhase Phase,
    int CompletedUnits,
    int TotalUnits,
    string Announcement);

/// <summary>
/// Executes the inexpensive and transcription preparation stages as a resumable sequence.
/// It never loads Whisper and OpenCV concurrently, and all stage outputs are persisted before
/// the corresponding checkpoint is marked complete.
/// </summary>
public sealed class CreatorPreparationWorkflow(
    IPipelineCheckpointStore checkpointStore,
    TimeProvider timeProvider,
    Func<IWhisperRuntime> whisperRuntimeFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<CreatorPreparationResult> RunAsync(
        Guid projectId,
        string projectDirectory,
        CreatorPreparationConfiguration configuration,
        IProgress<CreatorPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateConfiguration(configuration);
        var root = Path.GetFullPath(projectDirectory);
        var source = await LoadAsync<SourceIntakeResult>(
            Path.Combine(root, "manifests", "source-validated.json"),
            cancellationToken).ConfigureAwait(false);
        if (source.Checkpoint.ProjectId != projectId
            || source.Attestation.ProjectId != projectId
            || !source.Attestation.Accepted
            || !string.Equals(
                source.Attestation.SourceSha256,
                source.Source.SourceSha256,
                StringComparison.OrdinalIgnoreCase)
            || source.Checkpoint.Status != CheckpointStatus.Complete)
        {
            throw new InvalidDataException("The selected project's source checkpoint is invalid.");
        }

        await VerifyImmutableSourceAsync(source.Source, root, cancellationToken).ConfigureAwait(false);

        var phaseProgress = new InlineProgress<PipelineProgress>(item => progress?.Report(new(
            item.Phase,
            item.Status == CheckpointStatus.Complete ? 1 : 0,
            1,
            item.Announcement)));
        var coordinator = new SequentialPipelineCoordinator(
            [
                new AudioAnalysisPhase(
                    source,
                    root,
                    configuration,
                    whisperRuntimeFactory,
                    timeProvider,
                    progress),
                new VisualIndexPhase(source, root, configuration, timeProvider, progress),
            ],
            checkpointStore,
            timeProvider,
            phaseProgress);
        await coordinator.RunAsync(projectId, cancellationToken).ConfigureAwait(false);

        var audio = await LoadAsync<AudioAnalysisCompletionManifest>(
            Path.Combine(root, "manifests", "audio-analysis-complete.json"),
            cancellationToken).ConfigureAwait(false);
        var visual = await LoadAsync<VisualIndexManifest>(
            Path.Combine(root, "manifests", "visual-index.json"),
            cancellationToken).ConfigureAwait(false);
        if (audio.ProjectId != projectId || visual.ProjectId != projectId
            || !string.Equals(audio.SourceSha256, source.Source.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(visual.SourceSha256, source.Source.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A preparation manifest does not belong to the selected immutable source.");
        }

        return new(audio, visual);
    }

    private static void ValidateConfiguration(CreatorPreparationConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.FfmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.WhisperModel);
        if (!File.Exists(configuration.Whisper.PythonExecutable))
        {
            throw new FileNotFoundException(
                "The isolated ADFi Python runtime is missing. Run the local-AI installer first.",
                configuration.Whisper.PythonExecutable);
        }

        if (!File.Exists(configuration.Whisper.WorkerScriptPath))
        {
            throw new FileNotFoundException("The ADFi Whisper worker is missing.", configuration.Whisper.WorkerScriptPath);
        }

        if (configuration.MaximumImageDimension is < 320 or > 1920
            || configuration.MaximumFramesPerScene is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
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

    private static async Task VerifyImmutableSourceAsync(
        ImmutableSourceMetadata source,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var path = LocalSourcePolicy.ValidateExistingLocalVideo(source.SourcePath);
        var info = new FileInfo(path);
        if (info.Length != source.SourceSizeBytes
            || !string.Equals(
                await HashAsync(path, cancellationToken).ConfigureAwait(false),
                source.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The source video changed after authorization. Select it as a new project instead of resuming.");
        }

        var drive = new DriveInfo(Path.GetPathRoot(projectDirectory)!);
        if (drive.AvailableFreeSpace < source.EstimatedTemporaryBytes)
        {
            throw new IOException(
                $"Insufficient temporary disk space to resume safely. Required: {source.EstimatedTemporaryBytes} bytes; available: {drive.AvailableFreeSpace} bytes.");
        }
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
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
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
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private sealed class AudioAnalysisPhase(
        SourceIntakeResult source,
        string projectDirectory,
        CreatorPreparationConfiguration configuration,
        Func<IWhisperRuntime> runtimeFactory,
        TimeProvider timeProvider,
        IProgress<CreatorPreparationProgress>? progress) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => AdficationPhase.AudioCharacterizationAndDialogueSegmentation;

        public Task<bool> VerifyCheckpointAsync(
            PipelineCheckpoint checkpoint,
            CancellationToken cancellationToken) =>
            VerifyManifestAsync(
                checkpoint,
                projectDirectory,
                "manifests/audio-analysis-complete.json",
                cancellationToken);

        public async Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            var audio = await LoadOrPrepareAudioAsync(cancellationToken).ConfigureAwait(false);
            await using var runtime = runtimeFactory();
            var transcriptionProgress = new InlineProgress<TranscriptionProgress>(item => progress?.Report(new(
                Phase,
                item.CompletedChunks,
                item.TotalChunks,
                item.Announcement)));
            var transcript = await new SequentialTranscriptionService(
                    runtime,
                    timeProvider,
                    transcriptionProgress)
                .TranscribeAsync(
                    audio,
                    projectDirectory,
                    configuration.WhisperModel,
                    source.Source.Probe.Duration,
                    cancellationToken).ConfigureAwait(false);
            var sourceAudio = AudioTimelineComposer.Combine(
                source.Source.Probe.Duration,
                audio.Characterization.InitialAudioTimeline,
                transcript,
                TimeSpan.FromMilliseconds(120));
            var narrationWindows = NarrationWindowPlanner.FindQuietWindows(
                source.Source.Probe.Duration,
                sourceAudio,
                TimeSpan.FromMilliseconds(900),
                TimeSpan.FromMilliseconds(150));
            var completion = new AudioAnalysisCompletionManifest(
                projectId,
                source.Source.SourceSha256,
                configuration.WhisperModel,
                audio,
                transcript,
                sourceAudio,
                narrationWindows,
                timeProvider.GetUtcNow());
            const string relative = "manifests/audio-analysis-complete.json";
            var absolute = Path.Combine(projectDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            await WriteAsync(absolute, completion, cancellationToken).ConfigureAwait(false);
            return new(
                projectId,
                Phase,
                CheckpointStatus.Complete,
                timeProvider.GetUtcNow(),
                relative,
                await HashAsync(absolute, cancellationToken).ConfigureAwait(false),
                null);
        }

        private async Task<AudioPreparationManifest> LoadOrPrepareAudioAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(projectDirectory, "manifests", "audio-prepared.json");
            if (File.Exists(path))
            {
                try
                {
                    var existing = await LoadAsync<AudioPreparationManifest>(path, cancellationToken)
                        .ConfigureAwait(false);
                    var allOutputsExist = string.Equals(
                                              existing.SourceSha256,
                                              source.Source.SourceSha256,
                                              StringComparison.OrdinalIgnoreCase)
                                          && File.Exists(Path.Combine(
                                              projectDirectory,
                                              existing.AnalysisAudioRelativePath.Replace('/', Path.DirectorySeparatorChar)))
                                          && existing.Chunks.All(chunk => File.Exists(Path.Combine(
                                              projectDirectory,
                                              chunk.RelativeAudioPath.Replace('/', Path.DirectorySeparatorChar))));
                    if (allOutputsExist)
                    {
                        return existing;
                    }
                }
                catch (JsonException)
                {
                    // Rebuild an incomplete or corrupt pre-checkpoint manifest from the immutable source.
                }
            }

            progress?.Report(new(Phase, 0, 1, "Preparing normalized analysis audio and bounded chunks."));
            return await new AudioPreparationService(configuration.FfmpegPath, timeProvider)
                .PrepareAsync(projectId: source.Attestation.ProjectId, source.Source, projectDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class VisualIndexPhase(
        SourceIntakeResult source,
        string projectDirectory,
        CreatorPreparationConfiguration configuration,
        TimeProvider timeProvider,
        IProgress<CreatorPreparationProgress>? progress) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => AdficationPhase.VisualSegmentationAndKeyframes;

        public Task<bool> VerifyCheckpointAsync(
            PipelineCheckpoint checkpoint,
            CancellationToken cancellationToken) =>
            VerifyManifestAsync(
                checkpoint,
                projectDirectory,
                "manifests/visual-index.json",
                cancellationToken);

        public async Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            var audio = await LoadAsync<AudioAnalysisCompletionManifest>(
                Path.Combine(projectDirectory, "manifests", "audio-analysis-complete.json"),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new(Phase, 0, 1, "Building bounded scene index and keyframe sequences."));
            var visual = await new VisualIndexService(timeProvider).BuildAsync(
                projectId,
                source.Source,
                projectDirectory,
                configuration.MaximumImageDimension,
                configuration.MaximumFramesPerScene,
                audio.Transcript,
                audio.NarrationWindows,
                cancellationToken).ConfigureAwait(false);
            const string relative = "manifests/visual-index.json";
            var absolute = Path.Combine(projectDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            return new(
                projectId,
                Phase,
                CheckpointStatus.Complete,
                timeProvider.GetUtcNow(),
                relative,
                await HashAsync(absolute, cancellationToken).ConfigureAwait(false),
                null);
        }
    }

    private static async Task<bool> VerifyManifestAsync(
        PipelineCheckpoint checkpoint,
        string projectDirectory,
        string expectedRelativePath,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(checkpoint.ManifestRelativePath, expectedRelativePath, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(checkpoint.OutputSha256))
        {
            return false;
        }

        var path = Path.Combine(projectDirectory, expectedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path)
               && string.Equals(
                   checkpoint.OutputSha256,
                   await HashAsync(path, cancellationToken).ConfigureAwait(false),
                   StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
