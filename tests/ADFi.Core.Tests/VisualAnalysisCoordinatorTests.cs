using System.Text.Json;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.LocalAI;

namespace ADFi.Core.Tests;

public sealed class VisualAnalysisCoordinatorTests
{
    [Fact]
    public async Task PersistsEachEvidenceValidatedSceneAndUnloadsModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var evidenceIds = new List<string> { "frame-known" };
            var scene = new VisualScene(
                "scene-00000",
                new(TimeSpan.Zero, TimeSpan.FromSeconds(2)),
                [new("frame-known", TimeSpan.FromSeconds(1), "frame.jpg", 10, 10, 0, 0, false)],
                [],
                []);
            var response = JsonSerializer.Serialize(new
            {
                sceneId = scene.SceneId,
                observations = new[]
                {
                    new
                    {
                        observationId = "observation-1",
                        category = "action",
                        objectiveDescription = "A hand places a key on the table.",
                        evidenceIds,
                        certainty = 0.95m,
                        importance = 0.8m,
                        alreadyConveyedBySourceAudio = false,
                    },
                },
                potentialOnScreenText = Array.Empty<string>(),
                requiresAdditionalFrames = false,
                requiresExactOcr = false,
                uncertainty = (string?)null,
            });
            var runtime = new AnalysisRuntime(response);
            var coordinator = new VisualAnalysisCoordinator(
                runtime,
                "qwen3-vl:4b-instruct",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            var result = await coordinator.AnalyzeAsync(
                Guid.NewGuid(),
                root,
                [new(scene, new Dictionary<string, string> { ["frame-known"] = frame }, new Dictionary<string, string>(), [])],
                TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.True(runtime.Unloaded);
            Assert.True(File.Exists(Path.Combine(root, "visual", "analysis", "scene-00000.json")));
            Assert.Equal([frame], runtime.ImagePaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedEvidenceOnlyProducesAuditableConservativeEmptyResult()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var scene = new VisualScene(
                "scene-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                [new("frame-known", TimeSpan.Zero, "frame.jpg", 1, 1, 0, 0, false)], [], []);
            const string response = "{\"sceneId\":\"scene-00000\",\"observations\":[{\"observationId\":\"o\",\"category\":\"object\",\"objectiveDescription\":\"A car.\",\"evidenceIds\":[\"invented-frame\"],\"certainty\":0.9,\"importance\":0.8,\"alreadyConveyedBySourceAudio\":false}],\"potentialOnScreenText\":[],\"requiresAdditionalFrames\":false,\"requiresExactOcr\":false,\"uncertainty\":null}";
            var runtime = new AnalysisRuntime(response, response);
            var coordinator = new VisualAnalysisCoordinator(
                runtime, "qwen3-vl:4b-instruct",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            var result = await coordinator.AnalyzeAsync(
                Guid.NewGuid(), root,
                [new(scene, new Dictionary<string, string> { ["frame-known"] = frame }, new Dictionary<string, string>(), [])],
                TestContext.Current.CancellationToken);

            var analysis = Assert.Single(result);
            Assert.Empty(analysis.Observations);
            Assert.True(analysis.RequiresAdditionalFrames);
            Assert.Equal(
                "No evidence-valid visual observations remained after bounded model validation.",
                analysis.Uncertainty);
            Assert.True(runtime.Unloaded);
            Assert.Equal(2, runtime.Requests.Count);
            Assert.True(File.Exists(Path.Combine(root, "visual", "analysis", "rejected", "scene-00000-attempt-02.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalRetryKeepsOnlyIndependentlySupportedObservations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-conservative-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var scene = new VisualScene(
                "scene-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                [new("frame-known", TimeSpan.Zero, "frame.jpg", 1, 1, 0, 0, false)], [], []);
            const string mixedResponse = "{\"sceneId\":\"scene-00000\",\"observations\":[{\"observationId\":\"valid\",\"category\":\"object\",\"objectiveDescription\":\"A red door is closed.\",\"evidenceIds\":[\"frame-known\"],\"certainty\":0.9,\"importance\":0.8,\"alreadyConveyedBySourceAudio\":false},{\"observationId\":\"invalid\",\"category\":\"object\",\"objectiveDescription\":\"An unsupported object.\",\"evidenceIds\":[\"frame-know\"],\"certainty\":0.9,\"importance\":0.2,\"alreadyConveyedBySourceAudio\":false}],\"potentialOnScreenText\":[],\"requiresAdditionalFrames\":false,\"requiresExactOcr\":false,\"uncertainty\":null}";
            var runtime = new AnalysisRuntime(mixedResponse, mixedResponse);
            var coordinator = new VisualAnalysisCoordinator(
                runtime, "qwen3-vl:4b-instruct",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            var result = await coordinator.AnalyzeAsync(
                Guid.NewGuid(), root,
                [new(scene, new Dictionary<string, string> { ["frame-known"] = frame }, new Dictionary<string, string>(), [])],
                TestContext.Current.CancellationToken);

            var observation = Assert.Single(Assert.Single(result).Observations);
            Assert.Equal("valid", observation.ObservationId);
            Assert.Equal(["frame-known"], observation.EvidenceIds);
            Assert.Equal(2, runtime.Requests.Count);
            Assert.True(runtime.Unloaded);
            Assert.True(File.Exists(Path.Combine(root, "visual", "analysis", "rejected", "scene-00000-attempt-02.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OutOfRangeRoutingScoresAreDeterministicallyBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-score-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var scene = new VisualScene(
                "scene-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                [new("frame-known", TimeSpan.Zero, "frame.jpg", 1, 1, 0, 0, false)], [], []);
            const string response = "{\"sceneId\":\"scene-00000\",\"observations\":[{\"observationId\":\"o\",\"category\":\"object\",\"objectiveDescription\":\"A door.\",\"evidenceIds\":[\"frame-known\"],\"certainty\":-2,\"importance\":3,\"alreadyConveyedBySourceAudio\":false}],\"potentialOnScreenText\":[],\"requiresAdditionalFrames\":false,\"requiresExactOcr\":false,\"uncertainty\":null}";
            var runtime = new AnalysisRuntime(response);
            var coordinator = new VisualAnalysisCoordinator(
                runtime, "qwen-test",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            var result = await coordinator.AnalyzeAsync(
                Guid.NewGuid(), root,
                [new(scene, new Dictionary<string, string> { ["frame-known"] = frame }, new Dictionary<string, string>(), [])],
                TestContext.Current.CancellationToken);

            var observation = Assert.Single(Assert.Single(result).Observations);
            Assert.Equal(0m, observation.Certainty);
            Assert.Equal(1m, observation.Importance);
            Assert.Single(runtime.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransientRuntimeFailureGetsOneBoundedRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-runtime-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var scene = new VisualScene(
                "scene-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                [new("frame-known", TimeSpan.Zero, "frame.jpg", 1, 1, 0, 0, false)], [], []);
            const string response = "{\"sceneId\":\"scene-00000\",\"observations\":[],\"potentialOnScreenText\":[],\"requiresAdditionalFrames\":false,\"requiresExactOcr\":false,\"uncertainty\":null}";
            var runtime = new TransientFailureRuntime(response);
            var coordinator = new VisualAnalysisCoordinator(
                runtime, "qwen-test",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            var result = await coordinator.AnalyzeAsync(
                Guid.NewGuid(), root,
                [new(scene, new Dictionary<string, string> { ["frame-known"] = frame }, new Dictionary<string, string>(), [])],
                TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal(2, runtime.RequestCount);
            Assert.True(runtime.Unloaded);
            Assert.True(File.Exists(Path.Combine(root, "visual", "analysis", "rejected", "scene-00000-attempt-01.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedRuntimeFailureProducesConservativeUnresolvedScene()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-runtime-unavailable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var scene = new VisualScene(
                "scene-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                [new("frame-known", TimeSpan.Zero, "frame.jpg", 1, 1, 0, 0, false)], [], []);
            var runtime = new TransientFailureRuntime();
            var coordinator = new VisualAnalysisCoordinator(
                runtime, "qwen-test",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            var result = await coordinator.AnalyzeAsync(
                Guid.NewGuid(), root,
                [new(scene, new Dictionary<string, string> { ["frame-known"] = frame }, new Dictionary<string, string>(), [])],
                TestContext.Current.CancellationToken);

            var analysis = Assert.Single(result);
            Assert.Empty(analysis.Observations);
            Assert.True(analysis.RequiresAdditionalFrames);
            Assert.Equal("Visual model requests failed after one bounded retry.", analysis.Uncertainty);
            Assert.Equal(2, runtime.RequestCount);
            Assert.True(runtime.Unloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IncompleteJsonGetsOneBoundedConciseRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var scene = new VisualScene(
                "scene-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                [new("frame-known", TimeSpan.Zero, "frame.jpg", 1, 1, 0, 0, false)], [], []);
            const string complete = "{\"sceneId\":\"scene-00000\",\"observations\":[],\"potentialOnScreenText\":[],\"requiresAdditionalFrames\":false,\"requiresExactOcr\":false,\"uncertainty\":null}";
            var runtime = new AnalysisRuntime("{\"sceneId\":\"scene-00000\"", complete);
            var coordinator = new VisualAnalysisCoordinator(
                runtime, "qwen3-vl:2b-instruct",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            var result = await coordinator.AnalyzeAsync(
                Guid.NewGuid(), root,
                [new(scene, new Dictionary<string, string> { ["frame-known"] = frame }, new Dictionary<string, string>(), [])],
                TestContext.Current.CancellationToken);

            Assert.Single(result);
            Assert.Equal(2, runtime.Requests.Count);
            Assert.Equal(1_500, runtime.Requests[0].MaximumOutputTokens);
            Assert.Equal(2_400, runtime.Requests[1].MaximumOutputTokens);
            Assert.Contains("prior structured response was invalid", runtime.Requests[1].UserPrompt, StringComparison.OrdinalIgnoreCase);
            using var schema = JsonDocument.Parse(runtime.Requests[0].RequiredJsonSchema);
            Assert.Equal(6, schema.RootElement.GetProperty("properties").GetProperty("observations").GetProperty("maxItems").GetInt32());
            Assert.True(runtime.Unloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResumedAnalysisReusesOnlyMatchingPersistedScene()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var scene = new VisualScene(
                "scene-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                [new("frame-known", TimeSpan.Zero, "frame.jpg", 1, 1, 0, 0, false)], [], []);
            const string response = "{\"sceneId\":\"scene-00000\",\"observations\":[],\"potentialOnScreenText\":[],\"requiresAdditionalFrames\":false,\"requiresExactOcr\":false,\"uncertainty\":null}";
            var package = new VisualAnalysisPackage(
                scene,
                new Dictionary<string, string> { ["frame-known"] = frame },
                new Dictionary<string, string>(),
                []);
            var prompt = Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md");
            var firstRuntime = new AnalysisRuntime(response);
            _ = await new VisualAnalysisCoordinator(firstRuntime, "qwen-test", prompt, TimeProvider.System)
                .AnalyzeAsync(Guid.NewGuid(), root, [package], TestContext.Current.CancellationToken);

            var resumedRuntime = new AnalysisRuntime();
            var restored = await new VisualAnalysisCoordinator(resumedRuntime, "qwen-test", prompt, TimeProvider.System)
                .AnalyzeAsync(Guid.NewGuid(), root, [package], TestContext.Current.CancellationToken);

            Assert.Single(restored);
            Assert.Empty(resumedRuntime.Requests);
            Assert.False(resumedRuntime.Unloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OcrOnlyObservationIsNotMisrepresentedAsFrameEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-analysis-ocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var frame = Path.Combine(root, "frame.jpg");
        await File.WriteAllBytesAsync(frame, [1], TestContext.Current.CancellationToken);
        try
        {
            var scene = new VisualScene(
                "scene-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                [new("frame-known", TimeSpan.Zero, "frame.jpg", 1, 1, 0, 0, true)], [], []);
            const string response = "{\"sceneId\":\"scene-00000\",\"observations\":[{\"observationId\":\"text-1\",\"category\":\"onScreenText\",\"objectiveDescription\":\"A possible sign.\",\"evidenceIds\":[\"ocr-1\"],\"certainty\":0.5,\"importance\":0.8,\"alreadyConveyedBySourceAudio\":false}],\"potentialOnScreenText\":[\"EXIT\"],\"requiresAdditionalFrames\":false,\"requiresExactOcr\":true,\"uncertainty\":null}";
            var runtime = new AnalysisRuntime(response);
            var package = new VisualAnalysisPackage(
                scene,
                new Dictionary<string, string> { ["frame-known"] = frame },
                new Dictionary<string, string>(),
                [],
                [new(null, 0.5m, true, [], ["ocr-1"])]);
            var coordinator = new VisualAnalysisCoordinator(
                runtime,
                "qwen-test",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            var result = await coordinator.AnalyzeAsync(
                Guid.NewGuid(), root, [package], TestContext.Current.CancellationToken);

            Assert.Empty(Assert.Single(result).Observations);
            Assert.Empty(result[0].PotentialOnScreenText);
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

    private sealed class AnalysisRuntime(params string[] responses) : ILocalModelRuntime
    {
        private readonly Queue<string> queuedResponses = new(responses);

        public string RuntimeName => "test-runtime";
        public bool Unloaded { get; private set; }
        public IReadOnlyList<string> ImagePaths { get; private set; } = [];
        public List<LocalModelRequest> Requests { get; } = [];

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(string modelName, LocalModelRequest request, CancellationToken cancellationToken)
        {
            ImagePaths = request.ImagePaths;
            Requests.Add(request);
            return Task.FromResult(new LocalModelResponse(queuedResponses.Dequeue(), modelName, TimeSpan.FromSeconds(1)));
        }

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken)
        {
            Unloaded = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransientFailureRuntime(params string[] responses) : ILocalModelRuntime
    {
        private readonly Queue<string> queuedResponses = new(responses);

        public string RuntimeName => "test-runtime";
        public int RequestCount { get; private set; }
        public bool Unloaded { get; private set; }

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(
            string modelName,
            LocalModelRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1 || queuedResponses.Count == 0)
            {
                throw new InvalidOperationException("Ollama request failed transiently.");
            }

            return Task.FromResult(new LocalModelResponse(queuedResponses.Dequeue(), modelName, TimeSpan.Zero));
        }

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken)
        {
            Unloaded = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
