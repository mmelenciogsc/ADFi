using System.Text.Json;
using ADFi.Conversation;
using ADFi.LocalAI;

namespace ADFi.Core.Tests;

public sealed class ConversationPlanningTests
{
    [Theory]
    [InlineData("What happens at 2:14?", ConversationIntent.TrivialVqa, false, 134)]
    [InlineData("Play ten seconds around that", ConversationIntent.Playback, false, -1)]
    [InlineData("Undo that", ConversationIntent.Undo, true, -1)]
    [InlineData("Check the sign at 2:14, then fix it and play it", ConversationIntent.CompoundEdit, true, 134)]
    public void IntentClassificationControlsWhetherPlannerIsNeeded(
        string text,
        ConversationIntent intent,
        bool requiresPlanner,
        int expectedSeconds)
    {
        var result = ConversationIntentClassifier.Classify(text);
        Assert.Equal(intent, result.Intent);
        Assert.Equal(requiresPlanner, result.RequiresPlanner);
        Assert.Equal(expectedSeconds, result.ReferencedTime?.TotalSeconds ?? -1);
    }

    [Fact]
    public async Task PlannerRejectsUnregisteredToolsAndAlwaysUnloadsModel()
    {
        var root = FindRepositoryRoot();
        var response = JsonSerializer.Serialize(new TaskPlan(
            "1.0",
            "fix it",
            [new("task-1", "Run anything", "Done", [], ["Shell"])]));
        var runtime = new FakeRuntime(response);
        var planner = new LocalConversationPlanner(
            runtime,
            "qwen3-vl:4b-instruct",
            Path.Combine(root, "prompts", "ADFI_PLANNER_SYSTEM.md"),
            Path.Combine(root, "prompts", "semantic-tools.json"),
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(
            "fix it",
            new(Guid.NewGuid(), 1, TimeSpan.FromMinutes(3), ["scene-1"], []),
            TestContext.Current.CancellationToken));
        Assert.True(runtime.Unloaded);
    }

    [Fact]
    public async Task TrivialQuestionBypassesPlannerModel()
    {
        var runtime = new FakeRuntime("unused");
        var planner = new LocalConversationPlanner(
            runtime, "qwen3-vl:4b-instruct", "missing", "missing", TimeProvider.System);
        var result = await planner.PlanAsync(
            "Who is on the left?",
            new(Guid.NewGuid(), 1, TimeSpan.FromMinutes(1), [], []),
            TestContext.Current.CancellationToken);

        Assert.Null(result.Plan);
        Assert.Equal(0, runtime.GenerationCount);
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

    private sealed class FakeRuntime(string response) : ILocalModelRuntime
    {
        public string RuntimeName => "fake";
        public int GenerationCount { get; private set; }
        public bool Unloaded { get; private set; }

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalModelResponse> GenerateAsync(string modelName, LocalModelRequest request, CancellationToken cancellationToken)
        {
            GenerationCount++;
            return Task.FromResult(new LocalModelResponse(response, modelName, TimeSpan.Zero));
        }

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken)
        {
            Unloaded = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
