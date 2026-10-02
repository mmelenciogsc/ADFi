using System.Text.Json;
using ADFi.LocalAI;

namespace ADFi.Conversation;

public sealed record PlannerContext(
    Guid ProjectId,
    int ProjectVersion,
    TimeSpan MediaDuration,
    IReadOnlyList<string> KnownSceneIds,
    IReadOnlyList<string> UnresolvedSceneIds,
    IReadOnlySet<string>? AllowedToolNames = null);

public interface IConversationPlanner
{
    Task<PlannedConversation> PlanAsync(
        string userText,
        PlannerContext context,
        CancellationToken cancellationToken);
}

public sealed record PlannedConversation(
    ClassifiedConversationIntent Classification,
    TaskPlan? Plan,
    PromptDecisionProvenance? Provenance);

public sealed class LocalConversationPlanner(
    ILocalModelRuntime runtime,
    string modelName,
    string plannerPromptPath,
    string semanticToolsPath,
    TimeProvider timeProvider) : IConversationPlanner
{
    private const string TaskPlanSchema =
        """
        {"type":"object","required":["schemaVersion","userRequest","tasks"],"properties":{"schemaVersion":{"const":"1.0"},"userRequest":{"type":"string"},"tasks":{"type":"array","minItems":1,"maxItems":32,"items":{"type":"object","required":["taskId","objective","expectedOutput","dependencies","allowedTools"],"properties":{"taskId":{"type":"string","maxLength":80},"objective":{"type":"string","maxLength":500},"expectedOutput":{"type":"string","maxLength":500},"dependencies":{"type":"array","uniqueItems":true,"items":{"type":"string"}},"allowedTools":{"type":"array","minItems":1,"maxItems":1,"items":{"type":"string"}}},"additionalProperties":false}}},"additionalProperties":false}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<PlannedConversation> PlanAsync(
        string userText,
        PlannerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        ArgumentNullException.ThrowIfNull(context);
        var classification = ConversationIntentClassifier.Classify(userText);
        if (!classification.RequiresPlanner)
        {
            return new(classification, null, null);
        }

        var prompt = await PromptResourceLoader.LoadAsync(plannerPromptPath, cancellationToken)
            .ConfigureAwait(false);
        var tools = await SemanticToolContractLoader.LoadAndValidateAsync(semanticToolsPath, cancellationToken)
            .ConfigureAwait(false);
        var registeredTools = tools.Tools
            .Select(tool => tool.Name)
            .Where(tool => context.AllowedToolNames is null || context.AllowedToolNames.Contains(tool))
            .ToHashSet(StringComparer.Ordinal);
        if (registeredTools.Count == 0)
        {
            throw new InvalidOperationException("No semantic tools are enabled for this conversation mode.");
        }

        await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
        LocalModelResponse response;
        try
        {
            var input = JsonSerializer.Serialize(new
            {
                userRequest = userText,
                classifiedIntent = classification.Intent.ToString(),
                context.ProjectId,
                context.ProjectVersion,
                mediaDurationMilliseconds = checked((long)context.MediaDuration.TotalMilliseconds),
                context.KnownSceneIds,
                context.UnresolvedSceneIds,
                registeredSemanticTools = registeredTools.Order(StringComparer.Ordinal).ToArray(),
            });
            response = await runtime.GenerateAsync(
                modelName,
                new(prompt.Content, input, [], TaskPlanSchema, 1_600),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
        }

        var plan = JsonSerializer.Deserialize<TaskPlan>(response.Content, JsonOptions)
                   ?? throw new InvalidDataException("The local planner returned no TaskPlan.");
        TaskPlanValidator.Validate(plan, registeredTools);
        if (!string.Equals(plan.UserRequest, userText, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local planner changed the user's request.");
        }

        var provenance = new PromptDecisionProvenance(
            prompt.Id,
            prompt.Version.ToString(),
            prompt.Sha256,
            runtime.RuntimeName,
            response.Model,
            timeProvider.GetUtcNow());
        return new(classification, plan, provenance);
    }
}
