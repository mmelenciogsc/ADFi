using System.Text.Json;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class CreatorDraftNarrationWorkflowTests
{
    [Fact]
    public async Task VerifiedVisualFactsBecomeMeasuredNarrationAndAllThreePhasesResume()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ADFi.Tests", Guid.NewGuid().ToString("N"));
        var projectId = Guid.NewGuid();
        var project = Path.Combine(workspace, projectId.ToString("D"));
        const string sourceHash = "SOURCE-HASH";
        var mediaDuration = TimeSpan.FromSeconds(5);
        var sceneInterval = new MediaInterval(TimeSpan.Zero, mediaDuration);
        var frameTime = TimeSpan.FromSeconds(1);
        var frame = new KeyframeEvidence(
            "frame-1", frameTime, "visual/frames/frame.jpg", 640, 360, 0, 0, false);
        var laterFrame = new KeyframeEvidence(
            "frame-2", TimeSpan.FromSeconds(4), "visual/frames/frame-2.jpg", 640, 360, 0, 0, false);
        var source = new SourceIntakeResult(
            new(
                Path.Combine(workspace, "source.mp4"),
                sourceHash,
                1,
                DateTimeOffset.UnixEpoch,
                new("mp4", mediaDuration, 1, false, []),
                3),
            new(
                Guid.NewGuid(),
                projectId,
                DateTimeOffset.UnixEpoch,
                RightsAttestationPolicy.Version,
                RightsAttestationPolicy.Text,
                sourceHash,
                true),
            new(
                projectId,
                AdficationPhase.SourceIntakeAndAuthorization,
                CheckpointStatus.Complete,
                DateTimeOffset.UnixEpoch,
                "manifests/source-validated.json",
                sourceHash,
                null));
        var audio = new AudioAnalysisCompletionManifest(
            projectId,
            sourceHash,
            "tiny",
            new(
                projectId,
                sourceHash,
                "audio/analysis.wav",
                new(16_000, 1, null, null, []),
                [],
                DateTimeOffset.UnixEpoch),
            [],
            [new(sceneInterval, SourceAudioKind.QuietCandidate, 0.9m, "quiet-1")],
            [new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(4500))],
            DateTimeOffset.UnixEpoch);
        var visual = new VisualIndexManifest(
            projectId,
            sourceHash,
            TimeSpan.FromSeconds(1),
            640,
            3,
            [new("scene-00000", sceneInterval, [frame, laterFrame], [], ["narration-window-00000"])],
            DateTimeOffset.UnixEpoch);
        var faces = new FaceContinuityManifest(
            projectId, sourceHash, [], [], [], DateTimeOffset.UnixEpoch);
        var analysis = new VisualUnderstandingManifest(
            projectId,
            sourceHash,
            [
                new(
                    "scene-00000",
                    [
                        new(
                            "observation-1",
                            "action",
                            "A woman places a key on the table.",
                            ["frame-1", "frame-2"],
                            0.95m,
                            0.9m,
                            false),
                        new(
                            "observation-unsupported-title",
                            "text",
                            "The title 'Later Scene' appears.",
                            ["frame-1"],
                            0.95m,
                            0.9m,
                            false),
                    ],
                    [],
                    false,
                    false,
                    null,
                    new("ADFI_VISUAL_ANALYST_SYSTEM", "1.0.0", new string('A', 64),
                        "test", "qwen-test", DateTimeOffset.UnixEpoch)),
            ],
            DateTimeOffset.UnixEpoch);
        await WriteAsync(Path.Combine(project, "manifests", "source-validated.json"), source);
        await WriteAsync(Path.Combine(project, "manifests", "audio-analysis-complete.json"), audio);
        await WriteAsync(Path.Combine(project, "manifests", "visual-index.json"), visual);
        await WriteAsync(Path.Combine(project, "manifests", "face-continuity.json"), faces);
        await WriteAsync(Path.Combine(project, "manifests", "visual-analysis-complete.json"), analysis);

        var responses = new Queue<string>(
        [
            "{\"text\":\"A woman places a key on the table.\",\"evidenceIds\":[\"frame-1\",\"frame-2\"],\"uncertainty\":null,\"reason\":\"The silent action changes the scene.\"}",
        ]);
        var runtimeCreations = 0;
        var synthesisCount = 0;
        var workflow = new CreatorDraftNarrationWorkflow(
            new FilePipelineCheckpointStore(workspace),
            TimeProvider.System,
            () =>
            {
                runtimeCreations++;
                return new QueueRuntime(responses);
            },
            root => new FixedNarrationSynthesizer(root, () => synthesisCount++));
        var configuration = new CreatorDraftNarrationConfiguration(
            "qwen-test",
            Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_AD_WRITER_SYSTEM.md"),
            "voice-1",
            TimeSpan.FromMilliseconds(900),
            TimeSpan.FromMilliseconds(250));

        try
        {
            var first = await workflow.RunAsync(
                projectId, project, configuration, null, TestContext.Current.CancellationToken);
            var second = await workflow.RunAsync(
                projectId, project, configuration, null, TestContext.Current.CancellationToken);

            var opportunity = Assert.Single(first.Opportunities.Opportunities);
            Assert.Equal(frameTime, opportunity.Window.Start);
            Assert.DoesNotContain("Later Scene", opportunity.Reason, StringComparison.Ordinal);
            var narration = Assert.Single(first.Narration.Segments);
            Assert.True(narration.Fit.Fits);
            Assert.False(narration.RequiresUserReview);
            Assert.Equal("narration/cache/fixed.wav", narration.Segment.NarrationAssetRelativePath);
            Assert.Equal(first.Narration.SourceSha256, second.Narration.SourceSha256);
            Assert.Equal(2, runtimeCreations);
            Assert.Equal(1, synthesisCount);
            Assert.Empty(responses);
            Assert.True(File.Exists(Path.Combine(project, "manifests", "ad-opportunities.json")));
            Assert.True(File.Exists(Path.Combine(project, "manifests", "ad-draft-complete.json")));
            Assert.True(File.Exists(Path.Combine(project, "manifests", "narration-complete.json")));

            await File.WriteAllBytesAsync(
                Path.Combine(project, "narration", "cache", "fixed.wav"),
                [9, 9, 9],
                TestContext.Current.CancellationToken);
            var repaired = await workflow.RunAsync(
                projectId, project, configuration, null, TestContext.Current.CancellationToken);

            Assert.True(Assert.Single(repaired.Narration.Segments).Fit.Fits);
            Assert.Equal(3, runtimeCreations);
            Assert.Equal(2, synthesisCount);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static async Task WriteAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value), TestContext.Current.CancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ADFi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class QueueRuntime(Queue<string> responses) : ILocalModelRuntime
    {
        public string RuntimeName => "test-runtime";

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(
            string modelName,
            LocalModelRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new LocalModelResponse(responses.Dequeue(), modelName, TimeSpan.Zero));

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedNarrationSynthesizer(string root, Action onSynthesize) : INarrationSynthesizer
    {
        public async Task<NarrationSynthesisResult> SynthesizeAsync(
            NarrationSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            onSynthesize();
            var path = Path.Combine(root, "narration", "cache", "fixed.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4], cancellationToken);
            var duration = TimeSpan.FromSeconds(1.2);
            return new(
                request.SegmentId,
                path,
                duration,
                NarrationTimingPolicy.Evaluate(
                    duration, request.WindowDuration, request.BreathingRoom, request.AutoFitAttempt),
                false,
                PiperVoiceCatalog.Get(request.VoiceId));
        }
    }
}
