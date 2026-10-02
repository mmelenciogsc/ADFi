using ADFi.Conversation;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class AdDraftCoordinatorTests
{
    [Fact]
    public async Task OverlongDraftIsShortenedThenPersistedWithPromptProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var longText = string.Join(' ', Enumerable.Repeat("descriptive", 40));
            var runtime = new QueueRuntime(
                $"{{\"text\":\"{longText}\",\"evidenceIds\":[\"frame-1\"],\"uncertainty\":null,\"reason\":\"Important action\"}}",
                "{\"text\":\"She pockets a key.\",\"evidenceIds\":[\"frame-1\"],\"uncertainty\":null,\"reason\":\"Important action\"}");
            var coordinator = new AdDraftCoordinator(
                runtime,
                "qwen3-vl:4b-instruct",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_AD_WRITER_SYSTEM.md"));
            var opportunity = new ProposedAdInterval(
                "00001", "scene-1", new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)),
                3_000, ["frame-1"], [], "Action not conveyed in audio", 0.9m, 0.95m);
            var fact = new VisualFact(
                "fact-1", "scene-1", "She pockets a key.", 0.9m, 0.95m, false,
                TimeSpan.FromSeconds(1), null, ["frame-1"]);

            var result = await coordinator.DraftAsync(
                Guid.NewGuid(), root, [new(opportunity, [fact], [], [])], TestContext.Current.CancellationToken);

            var segment = Assert.Single(result);
            Assert.Equal("She pockets a key.", segment.Text);
            Assert.Equal("ADFI_AD_WRITER_SYSTEM", segment.PromptId);
            Assert.Equal(2, runtime.GenerationCount);
            Assert.True(runtime.Unloaded);
            Assert.True(File.Exists(Path.Combine(root, "manifests", "ad-draft-complete.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FutureCharacterNameIsRejectedAsSpoiler()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runtime = new QueueRuntime(
                "{\"text\":\"John enters.\",\"evidenceIds\":[\"frame-1\"],\"uncertainty\":null,\"reason\":\"Entrance\"}");
            var coordinator = new AdDraftCoordinator(
                runtime, "qwen3-vl:4b-instruct",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_AD_WRITER_SYSTEM.md"));
            var opportunity = new ProposedAdInterval(
                "1", "scene-1", new(TimeSpan.Zero, TimeSpan.FromSeconds(3)), 3_000,
                ["frame-1"], [], "Entrance", 0.9m, 0.9m);
            var entity = new ProjectEntity(
                "Person_01", EntityKind.Person, "the man",
                [new(IdentityEvidenceKind.TranscriptDialogue, "line-2", TimeSpan.FromSeconds(20), "John", 1m)]);
            var fact = new VisualFact(
                "fact", "scene-1", "A man enters.", 0.9m, 0.9m, false,
                TimeSpan.Zero, null, ["frame-1"]);

            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.DraftAsync(
                Guid.NewGuid(), root, [new(opportunity, [fact], [], [entity])],
                TestContext.Current.CancellationToken));
            Assert.True(runtime.Unloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedDraftRunReusesValidatedSegmentWithoutLoadingModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var opportunity = new ProposedAdInterval(
                "00001", "scene-1", new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)),
                3_000, ["frame-1"], [], "Important action", 0.9m, 0.95m);
            var package = new AdDraftPackage(
                opportunity,
                [new("fact-1", "scene-1", "She pockets a key.", 0.9m, 0.95m, false,
                    TimeSpan.FromSeconds(1), null, ["frame-1"])],
                [],
                []);
            var prompt = Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_AD_WRITER_SYSTEM.md");
            var firstRuntime = new QueueRuntime(
                "{\"text\":\"She pockets a key.\",\"evidenceIds\":[\"frame-1\"],\"uncertainty\":null,\"reason\":\"Important action\"}");
            _ = await new AdDraftCoordinator(firstRuntime, "qwen-test", prompt).DraftAsync(
                Guid.NewGuid(), root, [package], TestContext.Current.CancellationToken);
            File.Delete(Path.Combine(root, "manifests", "ad-draft-complete.json"));

            var resumedRuntime = new QueueRuntime();
            var resumed = await new AdDraftCoordinator(resumedRuntime, "qwen-test", prompt).DraftAsync(
                Guid.NewGuid(), root, [package], TestContext.Current.CancellationToken);

            Assert.Equal("She pockets a key.", Assert.Single(resumed).Text);
            Assert.Equal(0, resumedRuntime.GenerationCount);
            Assert.False(resumedRuntime.Unloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AllOverlongDraftsReturnShortestForMeasuredNarrationReview()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-draft-overrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            List<string> responseTexts =
            [
                "A winged creature rapidly descends toward a person lying motionless on the ground.",
                "A winged creature descends toward a person on the ground.",
                "A creature descends toward someone on the ground.",
            ];
            var responses = responseTexts.Select(text =>
                $"{{\"text\":\"{text}\",\"evidenceIds\":[\"frame-1\"],\"uncertainty\":null,\"reason\":\"Visible action\"}}")
                .ToArray();
            var runtime = new QueueRuntime(responses);
            var coordinator = new AdDraftCoordinator(
                runtime,
                "qwen-test",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_AD_WRITER_SYSTEM.md"));
            var opportunity = new ProposedAdInterval(
                "short", "scene-1", new(TimeSpan.Zero, TimeSpan.FromSeconds(1.25)), 1_250,
                ["frame-1"], [], "Visible action", 1m, 1m);
            var fact = new VisualFact(
                "fact", "scene-1", "A creature descends.", 1m, 1m, false,
                TimeSpan.Zero, null, ["frame-1"]);

            var result = await coordinator.DraftAsync(
                Guid.NewGuid(), root, [new(opportunity, [fact], [], [])], TestContext.Current.CancellationToken);

            Assert.Equal("A creature descends toward someone on the ground.", Assert.Single(result).Text);
            Assert.Equal(NarrationTimingPolicy.MaximumAutoFitAttempts, runtime.GenerationCount);
            Assert.True(runtime.Unloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "prompts")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class QueueRuntime(params string[] responses) : ILocalModelRuntime
    {
        private readonly Queue<string> queue = new(responses);
        public string RuntimeName => "test-runtime";
        public int GenerationCount { get; private set; }
        public bool Unloaded { get; private set; }

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(string modelName, LocalModelRequest request, CancellationToken cancellationToken)
        {
            GenerationCount++;
            return Task.FromResult(new LocalModelResponse(queue.Dequeue(), modelName, TimeSpan.Zero));
        }

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken)
        {
            Unloaded = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
