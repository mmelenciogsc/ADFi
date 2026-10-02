using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Diagnostics;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var options = DiagnosticOptions.Parse(args);
            var runner = new DiagnosticRunner(options);
            var result = await runner.RunAsync(cancellation.Token).ConfigureAwait(false);
            Console.WriteLine(FormattableString.Invariant($"FINAL_VIDEO={result.FinalVideoPath}"));
            Console.WriteLine(FormattableString.Invariant($"REPORT={result.ReportPath}"));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(FormattableString.Invariant($"DIAGNOSTIC_FAILED: {error.Message}"));
            return 1;
        }
    }

    private sealed class DiagnosticRunner(DiagnosticOptions options)
    {
        private readonly List<DiagnosticStageMeasurement> measurements = [];
        private readonly SystemHardwareProfiler profiler = new(
            options.ProjectsRoot,
            new ProcessHardwareCommandRunner());

        public async Task<DiagnosticRunResult> RunAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(options.ProjectsRoot);
            var sourceHash = await HashFileAsync(options.SourcePath, cancellationToken).ConfigureAwait(false);
            var projectId = StableProjectId(sourceHash);
            var projectDirectory = Path.Combine(options.ProjectsRoot, projectId.ToString("D"));
            var checkpointStore = new FilePipelineCheckpointStore(options.ProjectsRoot);
            var projectStore = new FileProjectVersionStore(options.ProjectsRoot);
            var probe = new FfprobeMediaProbe();
            var hardware = await MeasureAsync(
                    "hardware-profile",
                    () => profiler.ProfileAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            var tier = ModelTierPolicy.Select(hardware);
            WriteLine($"Selected {tier.Name} bounds with {tier.OllamaModel}.");

            var sourceManifestPath = Path.Combine(projectDirectory, "manifests", "source-validated.json");
            if (!File.Exists(sourceManifestPath))
            {
                var intake = await MeasureAsync(
                        "phase-0-source-intake",
                        () => new SourceIntakeService(probe, TimeProvider.System).ValidateAsync(
                            options.SourcePath,
                            projectDirectory,
                            options.DiagnosticUserId,
                            projectId,
                            rightsAccepted: true,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
                await checkpointStore.SaveAsync(intake.Checkpoint, cancellationToken).ConfigureAwait(false);
                await projectStore.InitializeAsync(
                    new(
                        projectId,
                        1,
                        intake.Source.SourceSha256,
                        intake.Source.Probe.Duration,
                        [],
                        "not-selected",
                        TimeProvider.System.GetUtcNow(),
                        null),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var current = await projectStore.LoadCurrentAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current.SourceSha256, sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The resumable diagnostic project belongs to different source content.");
                }

                WriteLine("Restoring the existing diagnostic project and verified checkpoints.");
            }

            var whisper = new WhisperRuntimeConfiguration(
                options.PythonExecutable,
                Path.Combine(options.RepositoryRoot, "src", "ADFi.Media", "Workers", "whisper_worker.py"),
                Path.Combine(options.ModelRoot, "whisper"));
            var preparation = new CreatorPreparationWorkflow(
                checkpointStore,
                TimeProvider.System,
                () => new OpenAiWhisperRuntime(whisper));
            var prepared = await MeasureAsync(
                    "phases-1-2-audio-and-scenes",
                    () => preparation.RunAsync(
                        projectId,
                        projectDirectory,
                        new(
                            "ffmpeg",
                            whisper,
                            "tiny",
                            tier.MaximumImageDimension,
                            tier.MaximumTemporalFrames),
                        new InlineProgress<CreatorPreparationProgress>(item => WriteLine(item.Announcement)),
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            var openCvRoot = Path.Combine(options.ModelRoot, "opencv");
            var visual = new CreatorVisualUnderstandingWorkflow(
                checkpointStore,
                TimeProvider.System,
                () => new OpenCvFaceContinuityAnalyzer(
                    Path.Combine(openCvRoot, "face_detection_yunet_2023mar.onnx"),
                    Path.Combine(openCvRoot, "face_recognition_sface_2021dec.onnx")),
                CreateOllamaRuntime,
                new TesseractProcessRunner());
            var understood = await MeasureAsync(
                    "phases-3-5-visual-understanding",
                    () => visual.RunAsync(
                        projectId,
                        projectDirectory,
                        new(
                            tier.OllamaModel,
                            Path.Combine(openCvRoot, "face_detection_yunet_2023mar.onnx"),
                            Path.Combine(openCvRoot, "face_recognition_sface_2021dec.onnx"),
                            ResolveTesseractPath(),
                            Prompt("ADFI_VISUAL_TEXT_SYSTEM.md"),
                            Prompt("ADFI_VISUAL_ANALYST_SYSTEM.md")),
                        new InlineProgress<CreatorVisualUnderstandingProgress>(item => WriteLine(item.Announcement)),
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            var drafting = new CreatorDraftNarrationWorkflow(
                checkpointStore,
                TimeProvider.System,
                CreateOllamaRuntime,
                root => new PiperNarrationService(
                    new(options.PythonExecutable, Path.Combine(options.ModelRoot, "piper")),
                    Path.Combine(root, "narration", "cache"),
                    new PiperProcessRunner()));
            var drafted = await MeasureAsync(
                    "phases-6-8-draft-and-narration",
                    () => drafting.RunAsync(
                        projectId,
                        projectDirectory,
                        new(
                            tier.OllamaModel,
                            Prompt("ADFI_AD_WRITER_SYSTEM.md"),
                            options.VoiceId,
                            TimeSpan.FromSeconds(2),
                            TimeSpan.FromMilliseconds(250)),
                        new InlineProgress<CreatorDraftNarrationProgress>(item => WriteLine(item.Announcement)),
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            await PersistInitialDraftAsync(projectStore, projectId, drafted, cancellationToken).ConfigureAwait(false);
            var snapshot = await projectStore.LoadCurrentAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(options.ApprovedReviewText))
            {
                snapshot = await ApplyApprovedDiagnosticReviewAsync(
                    projectStore,
                    snapshot,
                    drafted,
                    options,
                    projectDirectory,
                    cancellationToken).ConfigureAwait(false);
            }

            var preview = new CreatorPreviewWorkflow(new PreviewAssemblyService(
                new FinalRenderService("ffmpeg", probe),
                "ffmpeg",
                checkpointStore,
                TimeProvider.System));
            _ = await MeasureAsync(
                    "phase-9-review-preview",
                    () => preview.RunAsync(
                        projectId,
                        projectDirectory,
                        null,
                        new InlineProgress<CreatorPreviewProgress>(item => WriteLine(item.Announcement)),
                        cancellationToken,
                        snapshot),
                    cancellationToken)
                .ConfigureAwait(false);

            // This automated diagnostic review accepts no uncertain timing. It does not perform a
            // conversational mutation; Phase 10 remains a human review surface in the desktop app.
            if (snapshot.AdSegments.Any(item =>
                    !item.UserApproved
                    && (item.MeasuredNarrationDuration is not { } measured
                        || measured > item.Window.Duration - TimeSpan.FromMilliseconds(250))))
            {
                throw new InvalidOperationException("The diagnostic draft has narration timing that requires human review.");
            }

            var validator = new FinalProjectValidationService(
                projectStore,
                checkpointStore,
                probe,
                TimeProvider.System);
            var initialValidation = await MeasureAsync(
                    "phase-11-final-validation",
                    () => validator.RunAsync(projectId, projectDirectory, null, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            var blockers = initialValidation.Manifest.Issues
                .Where(item => !item.CanOverride)
                .ToArray();
            if (blockers.Length > 0)
            {
                throw new InvalidOperationException(
                    "Final validation found blocking issues: " +
                    string.Join(", ", blockers.Select(item => item.Code)));
            }

            var overrideIds = initialValidation.Manifest.Issues
                .Where(item => item.CanOverride)
                .Select(item => item.IssueId)
                .ToHashSet(StringComparer.Ordinal);
            var finalValidation = overrideIds.Count == 0
                ? initialValidation
                : await MeasureAsync(
                        "phase-11-explicit-diagnostic-warning-overrides",
                        () => validator.RunAsync(projectId, projectDirectory, overrideIds, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!finalValidation.ReadyForRender)
            {
                throw new InvalidOperationException("The diagnostic project did not pass the final rendering gate.");
            }

            var finalization = new CreatorFinalizationWorkflow(
                projectStore,
                checkpointStore,
                probe,
                new FinalRenderService("ffmpeg", probe),
                TimeProvider.System);
            var final = await MeasureAsync(
                    "phase-12-finalization",
                    () => finalization.RunAsync(
                        new(projectId, projectDirectory, IncludeNarrationAssets: true),
                        new InlineProgress<CreatorFinalizationProgress>(item => WriteLine(item.Announcement)),
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            var reportPath = Path.Combine(projectDirectory, "diagnostic-run.json");
            var report = new DiagnosticRunReport(
                "adfi.real-media-diagnostic.v1",
                options.Label,
                projectId,
                options.SourcePath,
                sourceHash,
                snapshot.MediaDuration,
                hardware,
                tier,
                prepared.AudioAnalysis.Transcript.Count,
                prepared.VisualIndex.Scenes.Count,
                understood.FaceContinuity.Clusters.Count,
                understood.VisualText.Scenes.Count,
                understood.SceneAnalysis.Scenes.Count,
                drafted.Opportunities.Opportunities.Count,
                drafted.Narration.Segments.Count,
                overrideIds.Order(StringComparer.Ordinal).ToArray(),
                final.FinalVideoPath,
                final.Manifest.FinalVideoSha256,
                measurements,
                TimeProvider.System.GetUtcNow());
            await WriteJsonAtomicallyAsync(reportPath, report, cancellationToken).ConfigureAwait(false);
            return new(final.FinalVideoPath, reportPath);
        }

        private OllamaModelRuntime CreateOllamaRuntime() => new(new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromMinutes(10),
        });

        private string Prompt(string fileName) => Path.Combine(options.RepositoryRoot, "prompts", fileName);

        private async Task<T> MeasureAsync<T>(
            string stage,
            Func<Task<T>> action,
            CancellationToken cancellationToken)
        {
            var started = TimeProvider.System.GetUtcNow();
            var before = await profiler.GetCurrentFreeResourcesAsync(cancellationToken).ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            WriteLine($"START {stage}");
            try
            {
                var result = await action().ConfigureAwait(false);
                timer.Stop();
                var after = await profiler.GetCurrentFreeResourcesAsync(cancellationToken).ConfigureAwait(false);
                measurements.Add(new(
                    stage,
                    started,
                    TimeProvider.System.GetUtcNow(),
                    timer.Elapsed,
                    before.FreeRamBytes,
                    after.FreeRamBytes,
                    before.FreeVramBytes,
                    after.FreeVramBytes,
                    "complete"));
                WriteLine($"COMPLETE {stage} in {timer.Elapsed}.");
                return result;
            }
            catch
            {
                timer.Stop();
                measurements.Add(new(
                    stage,
                    started,
                    TimeProvider.System.GetUtcNow(),
                    timer.Elapsed,
                    before.FreeRamBytes,
                    0,
                    before.FreeVramBytes,
                    0,
                    "failed"));
                throw;
            }
        }

        private static async Task PersistInitialDraftAsync(
            FileProjectVersionStore projectStore,
            Guid projectId,
            CreatorDraftNarrationResult result,
            CancellationToken cancellationToken)
        {
            var current = await projectStore.LoadCurrentAsync(projectId, cancellationToken).ConfigureAwait(false);
            var generated = result.Narration.Segments.Select(item => item.Segment).ToArray();
            if (current.AdSegments.Count > 0)
            {
                if (current.NarrationVoiceId == result.Narration.VoiceId
                    && SegmentsMatch(current.AdSegments, generated))
                {
                    return;
                }

                if (current.AdSegments.Any(item => item.UserApproved))
                {
                    throw new InvalidOperationException(
                        "The resumed diagnostic project contains reviewed state that will not be overwritten.");
                }
            }

            var now = TimeProvider.System.GetUtcNow();
            var next = current with
            {
                Version = current.Version + 1,
                AdSegments = generated,
                NarrationVoiceId = result.Narration.VoiceId,
                UpdatedAtUtc = now,
                RestoredFromVersion = null,
            };
            await projectStore.AppendAsync(
                next,
                new(
                    Guid.NewGuid(),
                    projectId,
                    current.Version,
                    next.Version,
                    current.AdSegments.Count == 0
                        ? ProjectMutationKind.GenerateInitialDraft
                        : ProjectMutationKind.RegenerateNarration,
                    current.AdSegments.Count == 0
                        ? "Generated the initial real-media diagnostic draft and measured narration."
                        : "Regenerated the unreviewed diagnostic draft after pipeline inputs changed.",
                    now),
                current.Version,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<ProjectSnapshot> ApplyApprovedDiagnosticReviewAsync(
            FileProjectVersionStore projectStore,
            ProjectSnapshot current,
            CreatorDraftNarrationResult result,
            DiagnosticOptions options,
            string projectDirectory,
            CancellationToken cancellationToken)
        {
            var unresolved = result.Narration.Segments
                .Where(item => item.RequiresUserReview || !item.Fit.Fits)
                .ToArray();
            if (unresolved.Length == 0)
            {
                return current;
            }

            if (unresolved.Length != 1)
            {
                throw new InvalidOperationException(
                    "--approved-review-text is only valid when exactly one narration segment requires review.");
            }

            var text = options.ApprovedReviewText!.Trim();
            var sourceSegment = unresolved[0].Segment;
            var synthesizer = new PiperNarrationService(
                new(options.PythonExecutable, Path.Combine(options.ModelRoot, "piper")),
                Path.Combine(projectDirectory, "narration", "cache"),
                new PiperProcessRunner());
            var synthesis = await synthesizer.SynthesizeAsync(
                new(
                    sourceSegment.SegmentId,
                    text,
                    result.Narration.VoiceId,
                    sourceSegment.Window.Duration,
                    TimeSpan.FromMilliseconds(250),
                    1),
                cancellationToken).ConfigureAwait(false);
            if (!synthesis.Fit.Fits)
            {
                throw new InvalidOperationException(
                    $"The explicitly reviewed narration still exceeds its window: {synthesis.MeasuredDuration} rendered, "
                    + $"{synthesis.Fit.Available} available.");
            }

            var relativeAudio = Path.GetRelativePath(projectDirectory, synthesis.AudioPath).Replace('\\', '/');
            if (relativeAudio.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relativeAudio))
            {
                throw new InvalidDataException("Reviewed narration was written outside the diagnostic project.");
            }

            var reviewed = sourceSegment with
            {
                Text = text,
                NarrationAssetRelativePath = relativeAudio,
                MeasuredNarrationDuration = synthesis.MeasuredDuration,
                UserApproved = true,
            };
            var segments = current.AdSegments
                .Select(item => item.SegmentId == reviewed.SegmentId ? reviewed : item)
                .ToArray();
            if (!segments.Any(item => item.SegmentId == reviewed.SegmentId))
            {
                throw new InvalidDataException("The reviewed narration segment is not present in project state.");
            }

            var now = TimeProvider.System.GetUtcNow();
            var next = current with
            {
                Version = current.Version + 1,
                AdSegments = segments,
                UpdatedAtUtc = now,
                RestoredFromVersion = null,
            };
            await projectStore.AppendAsync(
                next,
                new(
                    Guid.NewGuid(),
                    current.ProjectId,
                    current.Version,
                    next.Version,
                    ProjectMutationKind.ReplaceAdSegment,
                    "Explicitly approved wording for the single timing-blocked real-media diagnostic segment.",
                    now),
                current.Version,
                cancellationToken).ConfigureAwait(false);
            WriteLine($"Applied explicit diagnostic review to {reviewed.SegmentId}: {text}");
            return next;
        }

        private static bool SegmentsMatch(IReadOnlyList<AdSegment> left, AdSegment[] right) =>
            left.Count == right.Length
            && left.Zip(right).All(pair =>
                pair.First.SegmentId == pair.Second.SegmentId
                && pair.First.SceneId == pair.Second.SceneId
                && pair.First.Window == pair.Second.Window
                && pair.First.Text == pair.Second.Text
                && pair.First.EvidenceIds.SequenceEqual(pair.Second.EvidenceIds, StringComparer.Ordinal)
                && pair.First.PromptId == pair.Second.PromptId
                && pair.First.PromptVersion == pair.Second.PromptVersion
                && pair.First.PromptSha256 == pair.Second.PromptSha256
                && pair.First.NarrationAssetRelativePath == pair.Second.NarrationAssetRelativePath
                && pair.First.MeasuredNarrationDuration == pair.Second.MeasuredNarrationDuration
                && pair.First.UserApproved == pair.Second.UserApproved
                && pair.First.InputFingerprint == pair.Second.InputFingerprint);

        private static void WriteLine(string message) =>
            Console.WriteLine(FormattableString.Invariant($"{DateTimeOffset.Now:HH:mm:ss}  {message}"));
    }

    private sealed record DiagnosticOptions(
        string SourcePath,
        string Label,
        string RepositoryRoot,
        string ProjectsRoot,
        string PythonExecutable,
        string ModelRoot,
        string VoiceId,
        string? ApprovedReviewText,
        Guid DiagnosticUserId)
    {
        public static DiagnosticOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rightsConfirmed = false;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--rights-confirmed")
                {
                    rightsConfirmed = true;
                    continue;
                }

                if (!args[index].StartsWith("--", StringComparison.Ordinal)
                    || index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Invalid diagnostic argument: {args[index]}");
                }

                values[args[index]] = args[++index];
            }

            if (!rightsConfirmed)
            {
                throw new InvalidOperationException(
                    "Pass --rights-confirmed only after confirming ownership or authorization for this local media.");
            }

            var source = Required(values, "--source");
            _ = LocalSourcePolicy.ValidateExistingLocalVideo(source);
            var repository = Path.GetFullPath(values.GetValueOrDefault("--repository-root", Directory.GetCurrentDirectory()));
            if (!File.Exists(Path.Combine(repository, "ADFi.slnx")))
            {
                throw new DirectoryNotFoundException("The ADFi repository root is invalid.");
            }

            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var adfiLocal = Path.Combine(localData, "ADFi");
            return new(
                Path.GetFullPath(source),
                values.GetValueOrDefault("--label", Path.GetFileNameWithoutExtension(source)),
                repository,
                Path.GetFullPath(values.GetValueOrDefault(
                    "--projects-root",
                    Path.Combine(repository, "artifacts", "diagnostics", "projects"))),
                Path.GetFullPath(values.GetValueOrDefault(
                    "--python",
                    Path.Combine(adfiLocal, "runtime", "python", "Scripts", "python.exe"))),
                Path.GetFullPath(values.GetValueOrDefault(
                    "--model-root",
                    Path.Combine(adfiLocal, "models"))),
                values.GetValueOrDefault("--voice", "voice-1"),
                values.GetValueOrDefault("--approved-review-text"),
                Guid.Parse(values.GetValueOrDefault("--diagnostic-user-id", "00000000-0000-0000-0000-000000000001")));
        }

        private static string Required(Dictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"Missing required argument {key}.");
    }

    private sealed record DiagnosticStageMeasurement(
        string Stage,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        TimeSpan Elapsed,
        long FreeRamBeforeBytes,
        long FreeRamAfterBytes,
        long FreeVramBeforeBytes,
        long FreeVramAfterBytes,
        string State);

    private sealed record DiagnosticRunReport(
        string SchemaVersion,
        string Label,
        Guid ProjectId,
        string SourcePath,
        string SourceSha256,
        TimeSpan Duration,
        HardwareProfile Hardware,
        ModelTier SelectedTier,
        int TranscriptSegments,
        int VisualScenes,
        int RecurringPersonClusters,
        int VisualTextScenes,
        int AnalyzedScenes,
        int ProposedAdIntervals,
        int NarratedSegments,
        IReadOnlyList<string> ExplicitDiagnosticWarningOverrideIds,
        string FinalVideoPath,
        string FinalVideoSha256,
        IReadOnlyList<DiagnosticStageMeasurement> Measurements,
        DateTimeOffset CompletedAtUtc);

    private sealed record DiagnosticRunResult(string FinalVideoPath, string ReportPath);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static string ResolveTesseractPath()
    {
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Tesseract-OCR",
            "tesseract.exe");
        return File.Exists(installed) ? installed : "tesseract";
    }

    private static Guid StableProjectId(string sourceSha256)
    {
        var bytes = Convert.FromHexString(sourceSha256);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        try
        {
            await using (var stream = new FileStream(
                             partial,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }
}
