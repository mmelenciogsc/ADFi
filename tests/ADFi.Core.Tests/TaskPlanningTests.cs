using ADFi.Conversation;

namespace ADFi.Core.Tests;

public sealed class TaskPlanningTests
{
    [Fact]
    public void ForwardDependenciesAreRejected()
    {
        var plan = new TaskPlan(
            TaskPlanValidator.CurrentSchemaVersion,
            "Example",
            [new("task-1", "Do one thing", "One result", ["task-2"], [])]);

        Assert.Throws<InvalidOperationException>(() => TaskPlanValidator.Validate(plan));
    }

    [Fact]
    public void ModelPlanMustAuthorizeExactlyOneRegisteredToolPerTask()
    {
        var registered = new HashSet<string>(["GetScene", "ValidateProject"], StringComparer.Ordinal);
        var plan = new TaskPlan(
            TaskPlanValidator.CurrentSchemaVersion,
            "Example",
            [new("task-1", "Do one thing", "One result", [], ["GetScene", "ValidateProject"])]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            TaskPlanValidator.Validate(plan, registered));

        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelPlanHasABoundedTaskCount()
    {
        var plan = new TaskPlan(
            TaskPlanValidator.CurrentSchemaVersion,
            "Example",
            Enumerable.Range(1, TaskPlanValidator.MaximumTasksPerTurn + 1)
                .Select(index => new AtomicTask($"task-{index}", "One", "One", [], ["GetScene"]))
                .ToArray());

        Assert.Throws<InvalidOperationException>(() => TaskPlanValidator.Validate(plan));
    }

    [Fact]
    public async Task ProcessorPersistsEachResultAndStopsAtFirstFailure()
    {
        var executor = new RecordingExecutor(failTaskId: "task-2");
        var store = new RecordingStore();
        var processor = new SequentialTaskProcessor(executor, store);
        var plan = new TaskPlan(
            TaskPlanValidator.CurrentSchemaVersion,
            "Example",
            [
                new("task-1", "First", "First result", [], []),
                new("task-2", "Second", "Second result", ["task-1"], []),
                new("task-3", "Third", "Third result", ["task-2"], []),
            ]);

        var results = await processor.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(["task-1", "task-2"], results.Select(result => result.TaskId));
        Assert.Equal(["task-1", "task-2"], store.TaskIds);
    }

    private sealed class RecordingExecutor(string failTaskId) : IAtomicTaskExecutor
    {
        public Task<TaskResult> ExecuteAsync(AtomicTask task, CancellationToken cancellationToken)
        {
            var succeeded = task.TaskId != failTaskId;
            return Task.FromResult(new TaskResult(
                task.TaskId,
                succeeded,
                "{}",
                [],
                succeeded ? null : "EXPECTED_TEST_FAILURE"));
        }
    }

    private sealed class RecordingStore : ITaskResultStore
    {
        public List<string> TaskIds { get; } = [];

        public Task PersistAsync(TaskResult result, CancellationToken cancellationToken)
        {
            TaskIds.Add(result.TaskId);
            return Task.CompletedTask;
        }
    }
}
