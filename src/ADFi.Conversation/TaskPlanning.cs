using System.Text.Json.Serialization;

namespace ADFi.Conversation;

public sealed record AtomicTask(
    string TaskId,
    string Objective,
    string ExpectedOutput,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> AllowedTools);

public sealed record TaskPlan(
    string SchemaVersion,
    string UserRequest,
    IReadOnlyList<AtomicTask> Tasks);

public static class TaskPlanValidator
{
    public const string CurrentSchemaVersion = "1.0";

    public static void Validate(TaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsupported task-plan schema version.");
        }

        if (plan.Tasks.Count == 0)
        {
            throw new InvalidOperationException("A nontrivial request must contain at least one atomic task.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in plan.Tasks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(task.TaskId);
            ArgumentException.ThrowIfNullOrWhiteSpace(task.Objective);
            ArgumentException.ThrowIfNullOrWhiteSpace(task.ExpectedOutput);
            if (!ids.Add(task.TaskId))
            {
                throw new InvalidOperationException($"Duplicate task ID: {task.TaskId}");
            }

            foreach (var dependency in task.Dependencies)
            {
                if (!ids.Contains(dependency))
                {
                    throw new InvalidOperationException(
                        $"Task {task.TaskId} has a missing or forward dependency: {dependency}");
                }
            }
        }
    }
}

public sealed record TaskResult(
    string TaskId,
    bool Succeeded,
    string OutputJson,
    IReadOnlyList<string> EvidenceIds,
    string? ErrorCode);

public interface IAtomicTaskExecutor
{
    Task<TaskResult> ExecuteAsync(AtomicTask task, CancellationToken cancellationToken);
}

public interface ITaskResultStore
{
    Task PersistAsync(TaskResult result, CancellationToken cancellationToken);
}

public sealed class SequentialTaskProcessor(
    IAtomicTaskExecutor executor,
    ITaskResultStore resultStore)
{
    public async Task<IReadOnlyList<TaskResult>> ExecuteAsync(
        TaskPlan plan,
        CancellationToken cancellationToken)
    {
        TaskPlanValidator.Validate(plan);
        var results = new List<TaskResult>(plan.Tasks.Count);
        foreach (var task in plan.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await executor.ExecuteAsync(task, cancellationToken).ConfigureAwait(false);
            await resultStore.PersistAsync(result, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.Succeeded)
            {
                break;
            }
        }

        return results;
    }
}
