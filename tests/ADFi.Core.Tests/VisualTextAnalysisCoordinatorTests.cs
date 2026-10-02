using System.Text;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class VisualTextAnalysisCoordinatorTests
{
    [Fact]
    public async Task AnalyzeClipsRoundedModelCropToImageBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-visual-text-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "visual", "frames"));
        var promptPath = Path.Combine(root, "prompt.md");
        await File.WriteAllTextAsync(
            promptPath,
            "---\nid: TEST_VISUAL_TEXT\nversion: 1.0.0\noutput_schema: test-v1\n---\nLocate text.",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "visual", "frames", "frame.jpg"),
            [1],
            TestContext.Current.CancellationToken);
        var projectId = Guid.NewGuid();
        const string sourceHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var frame = new KeyframeEvidence(
            "frame-1", TimeSpan.Zero, "visual/frames/frame.jpg", 640, 360, 0, 0, true);
        var index = new VisualIndexManifest(
            projectId,
            sourceHash,
            TimeSpan.FromSeconds(1),
            640,
            1,
            [new("scene-1", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)), [frame], [], [])],
            DateTimeOffset.UnixEpoch);
        var runtime = new FixedRuntime(
            """{"sceneId":"scene-1","candidates":[{"candidateId":"text-1","frameEvidenceId":"frame-1","text":"","confidence":0.5,"meaningful":false,"exactCharactersRequired":false,"region":{"x":0.82,"y":0.9,"width":0.2,"height":0.2}}]}""");
        var coordinator = new VisualTextAnalysisCoordinator(
            runtime,
            "qwen-test",
            promptPath,
            new TesseractOcrService("unused", new UnusedOcrRunner()),
            TimeProvider.System);

        try
        {
            var result = await coordinator.AnalyzeAsync(
                projectId,
                sourceHash,
                index,
                root,
                TestContext.Current.CancellationToken);

            var region = Assert.Single(Assert.Single(result.Scenes).VlmCandidates).Region;
            Assert.NotNull(region);
            Assert.Equal(0.18m, region.Width);
            Assert.Equal(0.1m, region.Height);
            Assert.True(runtime.Unloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedEvidenceGetsOneBoundedRetryAndPersistsRejectedOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-visual-text-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "visual", "frames"));
        var promptPath = Path.Combine(root, "prompt.md");
        await File.WriteAllTextAsync(
            promptPath,
            "---\nid: TEST_VISUAL_TEXT\nversion: 1.0.0\noutput_schema: test-v1\n---\nLocate text.",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "visual", "frames", "frame.jpg"),
            [1],
            TestContext.Current.CancellationToken);
        var projectId = Guid.NewGuid();
        const string sourceHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var frame = new KeyframeEvidence(
            "frame-known", TimeSpan.Zero, "visual/frames/frame.jpg", 640, 360, 0, 0, true);
        var index = new VisualIndexManifest(
            projectId,
            sourceHash,
            TimeSpan.FromSeconds(1),
            640,
            1,
            [new("scene-1", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)), [frame], [], [])],
            DateTimeOffset.UnixEpoch);
        const string invalid = """{"sceneId":"scene-1","candidates":[{"candidateId":"text-1","frameEvidenceId":"invented-frame","text":"EXIT","confidence":0.8,"meaningful":true,"exactCharactersRequired":true,"region":null}]}""";
        const string valid = """{"sceneId":"scene-1","candidates":[]}""";
        var runtime = new FixedRuntime(invalid, valid);

        try
        {
            var result = await new VisualTextAnalysisCoordinator(
                    runtime,
                    "qwen-test",
                    promptPath,
                    new TesseractOcrService("unused", new UnusedOcrRunner()),
                    TimeProvider.System)
                .AnalyzeAsync(projectId, sourceHash, index, root, TestContext.Current.CancellationToken);

            Assert.Single(result.Scenes);
            Assert.Empty(result.Scenes[0].VlmCandidates);
            Assert.Equal(2, runtime.Requests.Count);
            Assert.Equal(900, runtime.Requests[0].MaximumOutputTokens);
            Assert.Equal(700, runtime.Requests[1].MaximumOutputTokens);
            Assert.Contains("prior structured response was invalid", runtime.Requests[1].UserPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dense credits", runtime.Requests[1].UserPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(root, "visual", "ocr", "rejected", "scene-1-attempt-01.json")));
            Assert.True(runtime.Unloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResumedAnalysisReusesOnlyMatchingPersistedLocator()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-visual-text-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "visual", "frames"));
        var promptPath = Path.Combine(root, "prompt.md");
        await File.WriteAllTextAsync(
            promptPath,
            "---\nid: TEST_VISUAL_TEXT\nversion: 1.0.0\noutput_schema: test-v1\n---\nLocate text.",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "visual", "frames", "frame.jpg"),
            [1],
            TestContext.Current.CancellationToken);
        var projectId = Guid.NewGuid();
        const string sourceHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var frame = new KeyframeEvidence(
            "frame-known", TimeSpan.Zero, "visual/frames/frame.jpg", 640, 360, 0, 0, true);
        var index = new VisualIndexManifest(
            projectId,
            sourceHash,
            TimeSpan.FromSeconds(1),
            640,
            1,
            [new("scene-1", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)), [frame], [], [])],
            DateTimeOffset.UnixEpoch);
        const string response = """{"sceneId":"scene-1","candidates":[]}""";

        try
        {
            var firstRuntime = new FixedRuntime(response);
            _ = await new VisualTextAnalysisCoordinator(
                    firstRuntime,
                    "qwen-test",
                    promptPath,
                    new TesseractOcrService("unused", new UnusedOcrRunner()),
                    TimeProvider.System)
                .AnalyzeAsync(projectId, sourceHash, index, root, TestContext.Current.CancellationToken);

            var resumedRuntime = new FixedRuntime();
            var resumed = await new VisualTextAnalysisCoordinator(
                    resumedRuntime,
                    "qwen-test",
                    promptPath,
                    new TesseractOcrService("unused", new UnusedOcrRunner()),
                    TimeProvider.System)
                .AnalyzeAsync(projectId, sourceHash, index, root, TestContext.Current.CancellationToken);

            Assert.Single(resumed.Scenes);
            Assert.Empty(resumedRuntime.Requests);
            Assert.False(resumedRuntime.Unloaded);
            Assert.NotNull(resumed.Scenes[0].InputFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoUnsupportedResponsesProduceAuditableConservativeFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-visual-text-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "visual", "frames"));
        var promptPath = Path.Combine(root, "prompt.md");
        await File.WriteAllTextAsync(
            promptPath,
            "---\nid: TEST_VISUAL_TEXT\nversion: 1.0.0\noutput_schema: test-v1\n---\nLocate text.",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "visual", "frames", "frame.jpg"),
            [1],
            TestContext.Current.CancellationToken);
        var projectId = Guid.NewGuid();
        const string sourceHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var frame = new KeyframeEvidence(
            "frame-known", TimeSpan.Zero, "visual/frames/frame.jpg", 640, 360, 0, 0, true);
        var index = new VisualIndexManifest(
            projectId,
            sourceHash,
            TimeSpan.FromSeconds(1),
            640,
            1,
            [new("scene-1", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)), [frame], [], [])],
            DateTimeOffset.UnixEpoch);
        const string invalid = """{"sceneId":"scene-1","candidates":[{"candidateId":"text-1","frameEvidenceId":"invented-frame","text":"EXIT","confidence":0.8,"meaningful":true,"exactCharactersRequired":true,"region":null}]}""";
        var runtime = new FixedRuntime(invalid, invalid);

        try
        {
            var result = await new VisualTextAnalysisCoordinator(
                    runtime,
                    "qwen-test",
                    promptPath,
                    new TesseractOcrService("unused", new UnusedOcrRunner()),
                    TimeProvider.System)
                .AnalyzeAsync(projectId, sourceHash, index, root, TestContext.Current.CancellationToken);

            var scene = Assert.Single(result.Scenes);
            Assert.Empty(scene.VlmCandidates);
            Assert.Empty(scene.ReconciledReadings);
            Assert.Contains("No text was promoted", scene.Uncertainty, StringComparison.Ordinal);
            Assert.Equal(2, runtime.Requests.Count);
            Assert.True(File.Exists(Path.Combine(root, "visual", "ocr", "rejected", "scene-1-attempt-01.json")));
            Assert.True(File.Exists(Path.Combine(root, "visual", "ocr", "rejected", "scene-1-attempt-02.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedRuntime(params string[] responses) : ILocalModelRuntime
    {
        private readonly Queue<string> queuedResponses = new(responses);

        public string RuntimeName => "test";
        public bool Unloaded { get; private set; }
        public List<LocalModelRequest> Requests { get; } = [];

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(
            string modelName,
            LocalModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new LocalModelResponse(queuedResponses.Dequeue(), modelName, TimeSpan.Zero));
        }

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken)
        {
            Unloaded = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedOcrRunner : IOcrProcessRunner
    {
        public Task<OcrProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("OCR should not run for a non-meaningful candidate.");
    }
}
