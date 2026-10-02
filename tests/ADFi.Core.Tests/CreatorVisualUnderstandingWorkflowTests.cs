using System.Text.Json;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.LocalAI;
using ADFi.Media;
using OpenCvSharp;

namespace ADFi.Core.Tests;

public sealed class CreatorVisualUnderstandingWorkflowTests
{
    [Fact]
    public async Task PreparedProjectBuildsAnonymousContinuityVerifiedTextAndGroundedScenesThenResumes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ADFi.Tests", Guid.NewGuid().ToString("N"));
        var projectId = Guid.NewGuid();
        var project = Path.Combine(root, projectId.ToString("D"));
        var frameRelative = "visual/frames/frame.jpg";
        var framePath = Path.Combine(project, "visual", "frames", "frame.jpg");
        var detector = Path.Combine(root, "detector.onnx");
        var recognizer = Path.Combine(root, "recognizer.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(framePath)!);
        await File.WriteAllBytesAsync(detector, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(recognizer, [1], TestContext.Current.CancellationToken);
        using (var image = new Mat(100, 100, MatType.CV_8UC3, Scalar.White))
        {
            Cv2.ImWrite(framePath, image);
        }

        const string sourceHash = "ABC123";
        var interval = new MediaInterval(TimeSpan.Zero, TimeSpan.FromSeconds(2));
        var source = new SourceIntakeResult(
            new(
                Path.Combine(root, "source.mp4"),
                sourceHash,
                1,
                DateTimeOffset.UnixEpoch,
                new("mp4", TimeSpan.FromSeconds(2), 1, false, []),
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
            [new("speech", "chunk", interval, "Someone speaks.", 1m, [])],
            [new(interval, SourceAudioKind.Dialogue, 1m, "audio")],
            [],
            DateTimeOffset.UnixEpoch);
        var frame = new KeyframeEvidence(
            "frame-1",
            TimeSpan.FromSeconds(1),
            frameRelative,
            100,
            100,
            0,
            0,
            true);
        var laterFrame = frame with
        {
            EvidenceId = "frame-2",
            SourceTime = TimeSpan.FromMilliseconds(1500),
        };
        var visual = new VisualIndexManifest(
            projectId,
            sourceHash,
            TimeSpan.FromSeconds(1),
            100,
            1,
            [new("scene-00000", interval, [frame, laterFrame], ["speech"], [])],
            DateTimeOffset.UnixEpoch);
        await WriteAsync(Path.Combine(project, "manifests", "source-validated.json"), source);
        await WriteAsync(Path.Combine(project, "manifests", "audio-analysis-complete.json"), audio);
        await WriteAsync(Path.Combine(project, "manifests", "visual-index.json"), visual);

        var responses = new Queue<string>(
        [
            """
            {"sceneId":"scene-00000","candidates":[{"candidateId":"text-1","frameEvidenceId":"frame-1","text":"EXIT","confidence":0.8,"meaningful":true,"exactCharactersRequired":true,"region":{"x":0.1,"y":0.1,"width":0.8,"height":0.4}}]}
            """,
            """
            {"sceneId":"scene-00000","observations":[{"observationId":"obs-1","category":"action","objectiveDescription":"Person 01 stands beside an EXIT sign.","evidenceIds":["frame-1"],"certainty":0.95,"importance":0.8,"alreadyConveyedBySourceAudio":false}],"potentialOnScreenText":["EXIT"],"requiresAdditionalFrames":false,"requiresExactOcr":false,"uncertainty":null}
            """,
        ]);
        var runtimeCreations = 0;
        var workflow = new CreatorVisualUnderstandingWorkflow(
            new FilePipelineCheckpointStore(root),
            TimeProvider.System,
            () => new FixedFaceAnalyzer(),
            () =>
            {
                runtimeCreations++;
                return new QueueModelRuntime(responses);
            },
            new FixedOcrRunner());
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new CreatorVisualUnderstandingConfiguration(
            "qwen-test",
            detector,
            recognizer,
            "tesseract",
            Path.Combine(repositoryRoot, "prompts", "ADFI_VISUAL_TEXT_SYSTEM.md"),
            Path.Combine(repositoryRoot, "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"));

        try
        {
            var first = await workflow.RunAsync(
                projectId,
                project,
                configuration,
                null,
                TestContext.Current.CancellationToken);
            var second = await workflow.RunAsync(
                projectId,
                project,
                configuration,
                null,
                TestContext.Current.CancellationToken);

            var cluster = Assert.Single(first.FaceContinuity.Clusters);
            Assert.Equal("Person_01", cluster.EntityId);
            Assert.Equal(2, cluster.ObservationCount);
            var textScene = Assert.Single(first.VisualText.Scenes);
            Assert.Equal("EXIT", Assert.Single(textScene.ReconciledReadings).SupportedText);
            var analysis = Assert.Single(first.SceneAnalysis.Scenes);
            Assert.Equal("frame-1", Assert.Single(analysis.Observations).EvidenceIds.Single());
            Assert.Equal(first.SceneAnalysis.ProjectId, second.SceneAnalysis.ProjectId);
            Assert.Equal(2, runtimeCreations);
            Assert.Empty(responses);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value),
            TestContext.Current.CancellationToken);
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

    private sealed class FixedFaceAnalyzer : IFaceContinuityAnalyzer
    {
        public IReadOnlyList<DetectedFace> DetectAndEmbed(string frameEvidenceId, string imagePath) =>
            [new(frameEvidenceId + "-face", frameEvidenceId, new Rect(10, 10, 20, 20), 0.99m, [1f, 0f])];

        public void Dispose()
        {
        }
    }

    private sealed class QueueModelRuntime(Queue<string> responses) : ILocalModelRuntime
    {
        public string RuntimeName => "test-runtime";

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(
            string modelName,
            LocalModelRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new LocalModelResponse(responses.Dequeue(), modelName, TimeSpan.Zero));

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedOcrRunner : IOcrProcessRunner
    {
        private const string Tsv =
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
            "5\t1\t1\t1\t1\t1\t0\t0\t20\t10\t98\tEXIT\n";

        public Task<OcrProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OcrProcessResult(0, Tsv, string.Empty));
    }
}
