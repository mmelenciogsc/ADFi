using System.Text.Json.Serialization;

namespace ADFi.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AdficationPhase>))]
public enum AdficationPhase
{
    SourceIntakeAndAuthorization = 0,
    AudioCharacterizationAndDialogueSegmentation = 1,
    VisualSegmentationAndKeyframes = 2,
    RecurringPersonContinuityAndCharacterGraph = 3,
    OcrAndVisualTextInterpretation = 4,
    VlmSceneAnalytics = 5,
    AdNecessityAndNarrationWindows = 6,
    DraftGeneration = 7,
    PiperNarrationAndTimingValidation = 8,
    InitialPreviewAssembly = 9,
    NaturalConversationReview = 10,
    FinalProjectValidation = 11,
    FinalMp4Rendering = 12,
    OptionalPostFinalizationActions = 13,
}

[JsonConverter(typeof(JsonStringEnumConverter<CheckpointStatus>))]
public enum CheckpointStatus
{
    Pending,
    InProgress,
    Complete,
    Failed,
}

public sealed record PipelineCheckpoint(
    Guid ProjectId,
    AdficationPhase Phase,
    CheckpointStatus Status,
    DateTimeOffset UpdatedAtUtc,
    string ManifestRelativePath,
    string? OutputSha256,
    string? FailureCode);

public interface IPipelineCheckpointStore
{
    Task<IReadOnlyList<PipelineCheckpoint>> LoadAsync(Guid projectId, CancellationToken cancellationToken);

    Task SaveAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken);
}

public interface IPipelinePhase
{
    AdficationPhase Phase { get; }

    Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken);
}

public sealed class SequentialPipelineCoordinator(
    IEnumerable<IPipelinePhase> phases,
    IPipelineCheckpointStore checkpointStore)
{
    private readonly IReadOnlyList<IPipelinePhase> orderedPhases = phases
        .OrderBy(candidate => candidate.Phase)
        .ToArray();

    public async Task RunAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var checkpoints = await checkpointStore.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
        var completed = checkpoints
            .Where(checkpoint => checkpoint.Status == CheckpointStatus.Complete)
            .Select(checkpoint => checkpoint.Phase)
            .ToHashSet();

        foreach (var phase in orderedPhases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.Contains(phase.Phase))
            {
                continue;
            }

            var result = await phase.ExecuteAsync(projectId, cancellationToken).ConfigureAwait(false);
            await checkpointStore.SaveAsync(result, cancellationToken).ConfigureAwait(false);

            if (result.Status != CheckpointStatus.Complete)
            {
                throw new InvalidOperationException(
                    $"Pipeline phase {phase.Phase} did not produce a complete checkpoint.");
            }
        }
    }
}

