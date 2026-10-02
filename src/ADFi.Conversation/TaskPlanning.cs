using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    public const int MaximumTasksPerTurn = 32;

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

        if (plan.Tasks.Count > MaximumTasksPerTurn)
        {
            throw new InvalidOperationException(
                $"A task plan may contain at most {MaximumTasksPerTurn} atomic tasks.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in plan.Tasks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(task.TaskId);
            ArgumentException.ThrowIfNullOrWhiteSpace(task.Objective);
            ArgumentException.ThrowIfNullOrWhiteSpace(task.ExpectedOutput);
            if (task.TaskId.Length > 80 || task.Objective.Length > 500 || task.ExpectedOutput.Length > 500)
            {
                throw new InvalidOperationException($"Task {task.TaskId} exceeds the bounded task-plan text limits.");
            }

            if (!ids.Add(task.TaskId))
            {
                throw new InvalidOperationException($"Duplicate task ID: {task.TaskId}");
            }

            if (task.Dependencies.Count != task.Dependencies.Distinct(StringComparer.Ordinal).Count())
            {
                throw new InvalidOperationException($"Task {task.TaskId} contains duplicate dependencies.");
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

    public static void Validate(
        TaskPlan plan,
        IReadOnlySet<string> registeredToolNames)
    {
        Validate(plan);
        ArgumentNullException.ThrowIfNull(registeredToolNames);
        foreach (var task in plan.Tasks)
        {
            if (task.AllowedTools.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Task {task.TaskId} must authorize exactly one semantic tool.");
            }

            var unsupported = task.AllowedTools
                .Where(tool => !registeredToolNames.Contains(tool))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (unsupported.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Task {task.TaskId} references unregistered tools: {string.Join(", ", unsupported)}");
            }
        }
    }
}

public enum ConversationIntent
{
    TrivialVqa,
    Navigate,
    Playback,
    ProposeEdit,
    CompoundEdit,
    ChangeVoice,
    Undo,
    Validate,
    Unknown,
}

public sealed record ClassifiedConversationIntent(
    ConversationIntent Intent,
    bool RequiresPlanner,
    TimeSpan? ReferencedTime);

public static partial class ConversationIntentClassifier
{
    public static ClassifiedConversationIntent Classify(string userText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        var normalized = userText.Trim();
        var time = TryReadTimestamp(normalized);
        if (UndoRegex().IsMatch(normalized))
        {
            return new(ConversationIntent.Undo, true, time);
        }

        if (VoiceRegex().IsMatch(normalized))
        {
            return new(ConversationIntent.ChangeVoice, true, time);
        }

        if (ValidationRegex().IsMatch(normalized))
        {
            return new(ConversationIntent.Validate, true, time);
        }

        if (EditRegex().Count(normalized) > 1 || CompoundConnectorRegex().IsMatch(normalized))
        {
            return new(ConversationIntent.CompoundEdit, true, time);
        }

        if (EditRegex().IsMatch(normalized))
        {
            return new(ConversationIntent.ProposeEdit, true, time);
        }

        if (PlaybackRegex().IsMatch(normalized))
        {
            return new(ConversationIntent.Playback, false, time);
        }

        if (NavigationRegex().IsMatch(normalized))
        {
            return new(ConversationIntent.Navigate, false, time);
        }

        if (QuestionRegex().IsMatch(normalized))
        {
            return new(ConversationIntent.TrivialVqa, false, time);
        }

        return new(ConversationIntent.Unknown, true, time);
    }

    private static TimeSpan? TryReadTimestamp(string value)
    {
        var match = TimestampRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        var minutes = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return seconds < 60 ? TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) : null;
    }

    [GeneratedRegex(@"\b(?:undo|revert)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UndoRegex();

    [GeneratedRegex(@"\b(?:use|choose|switch to|change to)\s+voice\b|\bvoice\s+[1-5]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VoiceRegex();

    [GeneratedRegex(@"\b(?:validate|check every|check all|find unresolved)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValidationRegex();

    [GeneratedRegex(@"\b(?:fix|change|rewrite|shorten|remove|add|replace|regenerate)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditRegex();

    [GeneratedRegex(@"\b(?:and then|then regenerate|then play|, then|; then)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompoundConnectorRegex();

    [GeneratedRegex(@"\b(?:play|preview)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaybackRegex();

    [GeneratedRegex(@"\b(?:next|previous)\s+(?:scene|description|unresolved)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NavigationRegex();

    [GeneratedRegex(@"^(?:what|who|where|when|is|does|do|can|which|how)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionRegex();

    [GeneratedRegex(@"\b(\d{1,4}):(\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();
}

public sealed record TaskResult(
    string TaskId,
    bool Succeeded,
    string OutputJson,
    IReadOnlyList<string> EvidenceIds,
    string? ErrorCode,
    string? ToolName = null,
    PromptDecisionProvenance? Provenance = null);

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
