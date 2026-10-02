using System.Text.Json;
using ADFi.LocalAI;

namespace ADFi.Conversation;

public sealed record AtomicTaskDecision(
    string TaskId,
    bool CanProceed,
    string Summary,
    string? ProposedText,
    string? RequestedVoiceId,
    IReadOnlyList<string> EvidenceIds,
    string? Uncertainty);

public sealed record ReasonedAtomicTask(
    AtomicTaskDecision Decision,
    PromptDecisionProvenance Provenance);

public interface IAtomicTaskReasoner
{
    Task<ReasonedAtomicTask> ReasonAsync(
        AtomicTask task,
        string userRequest,
        object boundedContext,
        IReadOnlySet<string> availableEvidenceIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Gives Qwen exactly one atomic task and a bounded persisted-evidence package. The
/// returned decision is advisory: deterministic host tools still perform all reads,
/// synthesis, validation, preview creation and mutations.
/// </summary>
public sealed class LocalAtomicTaskReasoner(
    ILocalModelRuntime runtime,
    string modelName,
    string taskExecutorPromptPath,
    TimeProvider timeProvider) : IAtomicTaskReasoner
{
    private const string OutputSchema =
        """
        {"type":"object","required":["taskId","canProceed","summary","proposedText","requestedVoiceId","evidenceIds","uncertainty"],"properties":{"taskId":{"type":"string"},"canProceed":{"type":"boolean"},"summary":{"type":"string"},"proposedText":{"type":["string","null"]},"requestedVoiceId":{"type":["string","null"]},"evidenceIds":{"type":"array","uniqueItems":true,"items":{"type":"string"}},"uncertainty":{"type":["string","null"]}},"additionalProperties":false}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ReasonedAtomicTask> ReasonAsync(
        AtomicTask task,
        string userRequest,
        object boundedContext,
        IReadOnlySet<string> availableEvidenceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(userRequest);
        ArgumentNullException.ThrowIfNull(boundedContext);
        ArgumentNullException.ThrowIfNull(availableEvidenceIds);
        if (task.AllowedTools.Count != 1)
        {
            throw new InvalidOperationException("An atomic executor call requires exactly one allowed semantic tool.");
        }

        var prompt = await PromptResourceLoader.LoadAsync(taskExecutorPromptPath, cancellationToken)
            .ConfigureAwait(false);
        await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
        LocalModelResponse response;
        try
        {
            response = await runtime.GenerateAsync(
                modelName,
                new(
                    prompt.Content,
                    JsonSerializer.Serialize(new
                    {
                        userRequest,
                        task = new
                        {
                            task.TaskId,
                            task.Objective,
                            task.ExpectedOutput,
                            allowedTool = task.AllowedTools[0],
                        },
                        persistedEvidenceContext = boundedContext,
                        instruction = "Return only the result of this task. Do not claim that deterministic host work has completed.",
                    }),
                    [],
                    OutputSchema,
                    800),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
        }

        var decision = JsonSerializer.Deserialize<AtomicTaskDecision>(response.Content, JsonOptions)
                       ?? throw new InvalidDataException("The atomic task executor returned no result.");
        if (!string.Equals(decision.TaskId, task.TaskId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The atomic task executor returned a result for another task.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Summary);
        if (decision.Summary.Length > 2_000
            || decision.ProposedText?.Length > 2_000
            || decision.EvidenceIds.Count > 64
            || decision.EvidenceIds.Any(id => !availableEvidenceIds.Contains(id)))
        {
            throw new InvalidDataException("The atomic task result exceeds its bounded evidence or text contract.");
        }

        if (decision.RequestedVoiceId is not null)
        {
            _ = ADFi.Media.PiperVoiceCatalog.Get(decision.RequestedVoiceId);
        }

        return new(
            decision,
            new(
                prompt.Id,
                prompt.Version.ToString(),
                prompt.Sha256,
                runtime.RuntimeName,
                response.Model,
                timeProvider.GetUtcNow()));
    }
}
