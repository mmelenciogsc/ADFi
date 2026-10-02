using ADFi.Conversation;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class NarrationAutoFitCoordinatorTests
{
    [Fact]
    public async Task OverrunIsShortenedSynthesizedAgainAndMeasured()
    {
        var runtime = new RewriteRuntime();
        var synth = new DurationSynthesizer(
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(1.5));
        var coordinator = new NarrationAutoFitCoordinator(
            synth,
            runtime,
            "qwen3-vl:4b-instruct",
            Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_AD_WRITER_SYSTEM.md"));

        var result = await coordinator.FitAsync(
            Segment(),
            "voice-1",
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);

        Assert.True(result.Fit.Fits);
        Assert.False(result.RequiresUserReview);
        Assert.Equal(2, result.SynthesisAttempts);
        Assert.Equal("She pockets the key.", result.Segment.Text);
        Assert.Equal(1, runtime.GenerationCount);
        Assert.Equal(1, runtime.UnloadCount);
        Assert.Contains("maximumWords", runtime.LastRequest!.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticEvidenceLossIsRejected()
    {
        var runtime = new RewriteRuntime("{\"text\":\"She moves.\",\"evidenceIds\":[\"frame-1\"]}");
        var synth = new DurationSynthesizer(TimeSpan.FromSeconds(4));
        var coordinator = new NarrationAutoFitCoordinator(
            synth,
            runtime,
            "qwen3-vl:4b-instruct",
            Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_AD_WRITER_SYSTEM.md"));

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.FitAsync(
            Segment(), "voice-1", TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
        Assert.Equal(1, runtime.UnloadCount);
    }

    [Fact]
    public async Task NonShorteningRewriteEndsAsUserReviewInsteadOfFailingProject()
    {
        var original = Segment();
        var escaped = System.Text.Json.JsonSerializer.Serialize(original.Text);
        var runtime = new RewriteRuntime(
            $"{{\"text\":{escaped},\"evidenceIds\":[\"frame-1\",\"frame-2\"]}}");
        var synth = new DurationSynthesizer(
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(4));
        var coordinator = new NarrationAutoFitCoordinator(
            synth,
            runtime,
            "qwen3-vl:4b-instruct",
            Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_AD_WRITER_SYSTEM.md"));

        var result = await coordinator.FitAsync(
            original,
            "voice-1",
            TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);

        Assert.True(result.RequiresUserReview);
        Assert.False(result.Fit.Fits);
        Assert.Equal(NarrationTimingPolicy.MaximumAutoFitAttempts, result.SynthesisAttempts);
        Assert.Equal(original.Text, result.Segment.Text);
        Assert.Equal(NarrationTimingPolicy.MaximumAutoFitAttempts - 1, runtime.GenerationCount);
    }

    private static AdSegment Segment() => new(
        "ad-1", "scene-1", new(TimeSpan.Zero, TimeSpan.FromSeconds(3)),
        "She slowly reaches forward and places a small brass key into her coat pocket.",
        ["frame-1", "frame-2"],
        "ADFI_AD_WRITER_SYSTEM", "1.0.0", new string('A', 64), null, null, false);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "prompts")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class DurationSynthesizer(params TimeSpan[] durations) : INarrationSynthesizer
    {
        private readonly Queue<TimeSpan> queue = new(durations);

        public Task<NarrationSynthesisResult> SynthesizeAsync(
            NarrationSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            var duration = queue.Dequeue();
            var fit = NarrationTimingPolicy.Evaluate(
                duration, request.WindowDuration, request.BreathingRoom, request.AutoFitAttempt);
            return Task.FromResult(new NarrationSynthesisResult(
                request.SegmentId, "narration.wav", duration, fit, false, PiperVoiceCatalog.Get(request.VoiceId)));
        }
    }

    private sealed class RewriteRuntime : ILocalModelRuntime
    {
        private readonly string response;

        public RewriteRuntime(string? response = null)
        {
            this.response = response
                            ?? "{\"text\":\"She pockets the key.\",\"evidenceIds\":[\"frame-1\",\"frame-2\"]}";
        }

        public string RuntimeName => "test-runtime";
        public int GenerationCount { get; private set; }
        public int UnloadCount { get; private set; }
        public LocalModelRequest? LastRequest { get; private set; }

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(string modelName, LocalModelRequest request, CancellationToken cancellationToken)
        {
            GenerationCount++;
            LastRequest = request;
            return Task.FromResult(new LocalModelResponse(response, modelName, TimeSpan.Zero));
        }

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken)
        {
            UnloadCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
