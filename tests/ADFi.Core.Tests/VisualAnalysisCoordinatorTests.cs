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
                "qwen3-vl:4b",
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
    public async Task UnsupportedEvidenceIdFailsAndStillUnloadsModel()
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
            var runtime = new AnalysisRuntime(response);
            var coordinator = new VisualAnalysisCoordinator(
                runtime, "qwen3-vl:4b",
                Path.Combine(FindRepositoryRoot(), "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md"),
                TimeProvider.System);

            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.AnalyzeAsync(
                Guid.NewGuid(), root,
                [new(scene, new Dictionary<string, string> { ["frame-known"] = frame }, new Dictionary<string, string>(), [])],
                TestContext.Current.CancellationToken));
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

    private sealed class AnalysisRuntime(string response) : ILocalModelRuntime
    {
        public string RuntimeName => "test-runtime";
        public bool Unloaded { get; private set; }
        public IReadOnlyList<string> ImagePaths { get; private set; } = [];

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(string modelName, LocalModelRequest request, CancellationToken cancellationToken)
        {
            ImagePaths = request.ImagePaths;
            return Task.FromResult(new LocalModelResponse(response, modelName, TimeSpan.FromSeconds(1)));
        }

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken)
        {
            Unloaded = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
