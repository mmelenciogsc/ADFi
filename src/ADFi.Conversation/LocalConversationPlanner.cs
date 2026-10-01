using System.Text.Json;
using ADFi.LocalAI;

namespace ADFi.Conversation;

public sealed record PlannerContext(
    Guid ProjectId,
    int ProjectVersion,
    TimeSpan MediaDuration,
    IReadOnlyList<string> KnownSceneIds,
    IReadOnlyList<string> UnresolvedSceneIds);

public sealed record PlannedConversation(
    ClassifiedConversationIntent Classification,
    TaskPlan? Plan,
    PromptDecisionProvenance? Provenance);

public sealed class LocalConversationPlanner(
    ILocalModelRuntime runtime,
    string modelName,
    string plannerPromptPath,
    string semanticToolsPath,
    TimeProvider timeProvider)
{
    private const string TaskPlanSchema =
        """
        {"type":"object","required":["schemaVersion","userRequest","tasks"],"properties":{"schemaVersion":{"const":"1.0"},"userRequest":{"type":"string"},"tasks":{"type":"array","minItems":1,"items":{"type":"object","required":["taskId","objective","expectedOutput","dependencies","allowedTools"],"properties":{"taskId":{"type":"string"},"objective":{"type":"string"},"expectedOutput":{"type":"string"},"dependencies":{"type":"array","items":{"type":"string"}},"allowedTools":{"type":"array","minItems":1,"items":{"type":"string"}}},"additionalProperties":false}}},"additionalProperties":false}
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
                registeredSemanticTools = tools.Tools.Select(tool => tool.Name).ToArray(),
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
        TaskPlanValidator.Validate(
            plan,
            tools.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal));
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
