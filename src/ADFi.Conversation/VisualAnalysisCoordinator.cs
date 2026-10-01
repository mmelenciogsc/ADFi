using System.Text.Json;
using ADFi.Core;
using ADFi.LocalAI;

namespace ADFi.Conversation;

public sealed record VisualObservation(
    string ObservationId,
    string Category,
    string ObjectiveDescription,
    IReadOnlyList<string> EvidenceIds,
    decimal Certainty,
    decimal Importance,
    bool AlreadyConveyedBySourceAudio);

public sealed record VisualSceneAnalysis(
    string SceneId,
    IReadOnlyList<VisualObservation> Observations,
    IReadOnlyList<string> PotentialOnScreenText,
    bool RequiresAdditionalFrames,
    bool RequiresExactOcr,
    string? Uncertainty,
    PromptDecisionProvenance Provenance);

public sealed record VisualAnalysisPackage(
    VisualScene Scene,
    IReadOnlyDictionary<string, string> AbsoluteFramePaths,
    IReadOnlyDictionary<string, string> TranscriptContext,
    IReadOnlyList<ProjectEntity> EstablishedEntities);

public sealed class VisualAnalysisCoordinator(
    ILocalModelRuntime runtime,
    string modelName,
    string analystPromptPath,
    TimeProvider timeProvider)
{
    private const string OutputSchema =
        """
        {"type":"object","required":["sceneId","observations","potentialOnScreenText","requiresAdditionalFrames","requiresExactOcr"],"properties":{"sceneId":{"type":"string"},"observations":{"type":"array","items":{"type":"object","required":["observationId","category","objectiveDescription","evidenceIds","certainty","importance","alreadyConveyedBySourceAudio"],"properties":{"observationId":{"type":"string"},"category":{"type":"string"},"objectiveDescription":{"type":"string"},"evidenceIds":{"type":"array","minItems":1,"items":{"type":"string"}},"certainty":{"type":"number","minimum":0,"maximum":1},"importance":{"type":"number","minimum":0,"maximum":1},"alreadyConveyedBySourceAudio":{"type":"boolean"}},"additionalProperties":false}},"potentialOnScreenText":{"type":"array","items":{"type":"string"}},"requiresAdditionalFrames":{"type":"boolean"},"requiresExactOcr":{"type":"boolean"},"uncertainty":{"type":["string","null"]}},"additionalProperties":false}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<VisualSceneAnalysis>> AnalyzeAsync(
        Guid projectId,
        string projectDirectory,
        IReadOnlyList<VisualAnalysisPackage> packages,
        CancellationToken cancellationToken)
    {
        var prompt = await PromptResourceLoader.LoadAsync(analystPromptPath, cancellationToken)
            .ConfigureAwait(false);
        await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
        var results = new List<VisualSceneAnalysis>(packages.Count);
        try
        {
            foreach (var package in packages.OrderBy(item => item.Scene.SourceInterval.Start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await runtime.GenerateAsync(
                    modelName,
                    new(
                        prompt.Content,
                        BuildInput(projectId, package),
                        package.Scene.Keyframes.Select(frame => RequiredFramePath(package, frame.EvidenceId)).ToArray(),
                        OutputSchema,
                        1_500),
                    cancellationToken).ConfigureAwait(false);
                var draft = JsonSerializer.Deserialize<VisualAnalysisDraft>(response.Content, JsonOptions)
                            ?? throw new InvalidDataException("The visual analyst returned no scene result.");
                var result = ValidateAndAttachProvenance(package, draft, prompt, response);
                await PersistAsync(projectDirectory, result, cancellationToken).ConfigureAwait(false);
                results.Add(result);
            }
        }
        finally
        {
            await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
        }

        return results;
    }

    private static string BuildInput(Guid projectId, VisualAnalysisPackage package)
    {
        var established = package.EstablishedEntities.Select(entity => new
        {
            entity.EntityId,
            entity.Kind,
            DisplayName = entity.DisplayNameAt(package.Scene.SourceInterval.Start),
        });
        return JsonSerializer.Serialize(new
        {
            projectId,
            scene = new
            {
                package.Scene.SceneId,
                startMilliseconds = checked((long)package.Scene.SourceInterval.Start.TotalMilliseconds),
                endMilliseconds = checked((long)package.Scene.SourceInterval.End.TotalMilliseconds),
                frames = package.Scene.Keyframes.Select(frame => new
                {
                    frame.EvidenceId,
                    sourceMilliseconds = checked((long)frame.SourceTime.TotalMilliseconds),
                    frame.PotentialText,
                }),
            },
            transcript = package.TranscriptContext,
            establishedEntities = established,
            instruction = "Report only facts visible in the supplied ordered frames. Cite their evidence IDs. Identify what is not already conveyed by the supplied transcript or source-sound context.",
        });
    }

    private VisualSceneAnalysis ValidateAndAttachProvenance(
        VisualAnalysisPackage package,
        VisualAnalysisDraft draft,
        PromptResource prompt,
        LocalModelResponse response)
    {
        if (!string.Equals(draft.SceneId, package.Scene.SceneId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The visual analysis references the wrong scene.");
        }

        var availableEvidence = package.Scene.Keyframes.Select(frame => frame.EvidenceId).ToHashSet(StringComparer.Ordinal);
        foreach (var observation in draft.Observations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.ObservationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.Category);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.ObjectiveDescription);
            if (observation.Certainty is < 0 or > 1 || observation.Importance is < 0 or > 1)
            {
                throw new InvalidDataException("Visual-analysis scores must be between zero and one.");
            }

            if (observation.EvidenceIds.Count == 0
                || observation.EvidenceIds.Any(id => !availableEvidence.Contains(id)))
            {
                throw new InvalidDataException("A visual observation cites unavailable frame evidence.");
            }
        }

        return new(
            draft.SceneId,
            draft.Observations,
            draft.PotentialOnScreenText,
            draft.RequiresAdditionalFrames,
            draft.RequiresExactOcr,
            draft.Uncertainty,
            new(
                prompt.Id,
                prompt.Version.ToString(),
                prompt.Sha256,
                runtime.RuntimeName,
                response.Model,
                timeProvider.GetUtcNow()));
    }

    private static string RequiredFramePath(VisualAnalysisPackage package, string evidenceId) =>
        package.AbsoluteFramePaths.TryGetValue(evidenceId, out var path) && File.Exists(path)
            ? Path.GetFullPath(path)
            : throw new FileNotFoundException($"Visual evidence {evidenceId} is missing.");

    private static async Task PersistAsync(
        string projectDirectory,
        VisualSceneAnalysis result,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Path.GetFullPath(projectDirectory),
            "visual",
            "analysis",
            result.SceneId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed record VisualAnalysisDraft(
        string SceneId,
        IReadOnlyList<VisualObservation> Observations,
        IReadOnlyList<string> PotentialOnScreenText,
        bool RequiresAdditionalFrames,
        bool RequiresExactOcr,
        string? Uncertainty);
}
