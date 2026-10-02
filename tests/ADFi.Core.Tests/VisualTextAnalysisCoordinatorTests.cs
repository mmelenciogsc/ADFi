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
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedRuntime(string content) : ILocalModelRuntime
    {
        public string RuntimeName => "test";

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(
            string modelName,
            LocalModelRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new LocalModelResponse(content, modelName, TimeSpan.Zero));

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

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
