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

public sealed record PipelineProgress(
    AdficationPhase Phase,
    CheckpointStatus Status,
    int PhaseNumber,
    int PhaseCount,
    string Announcement);

public sealed class SequentialPipelineCoordinator(
    IEnumerable<IPipelinePhase> phases,
    IPipelineCheckpointStore checkpointStore,
    TimeProvider? timeProvider = null,
    IProgress<PipelineProgress>? progress = null)
{
    private readonly IPipelinePhase[] orderedPhases = phases
        .OrderBy(candidate => candidate.Phase)
        .ToArray();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var checkpoints = await checkpointStore.LoadAsync(projectId, cancellationToken).ConfigureAwait(false);
        var completed = checkpoints
            .Where(checkpoint => checkpoint.Status == CheckpointStatus.Complete)
            .Select(checkpoint => checkpoint.Phase)
            .ToHashSet();

        for (var index = 0; index < orderedPhases.Length; index++)
        {
            var phase = orderedPhases[index];
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.Contains(phase.Phase))
            {
                continue;
            }

            var inProgress = new PipelineCheckpoint(
                projectId,
                phase.Phase,
                CheckpointStatus.InProgress,
                clock.GetUtcNow(),
                string.Empty,
                null,
                null);
            await checkpointStore.SaveAsync(inProgress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(
                phase.Phase,
                CheckpointStatus.InProgress,
                index + 1,
                orderedPhases.Length,
                AnnouncementFor(phase.Phase, complete: false)));

            try
            {
                var result = await phase.ExecuteAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (result.ProjectId != projectId || result.Phase != phase.Phase)
                {
                    throw new InvalidDataException("Pipeline phase returned a checkpoint for a different project or phase.");
                }

                if (result.Status != CheckpointStatus.Complete)
                {
                    throw new InvalidOperationException(
                        $"Pipeline phase {phase.Phase} did not produce a complete checkpoint.");
                }

                await checkpointStore.SaveAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var failed = inProgress with
                {
                    Status = CheckpointStatus.Failed,
                    UpdatedAtUtc = clock.GetUtcNow(),
                    FailureCode = error is OperationCanceledException
                        ? "CANCELLED"
                        : $"{phase.Phase.ToString().ToUpperInvariant()}_FAILED",
                };
                await checkpointStore.SaveAsync(failed, CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new(
                    phase.Phase,
                    CheckpointStatus.Failed,
                    index + 1,
                    orderedPhases.Length,
                    $"{phase.Phase} stopped: {error.GetType().Name}."));
                throw;
            }

            progress?.Report(new(
                phase.Phase,
                CheckpointStatus.Complete,
                index + 1,
                orderedPhases.Length,
                AnnouncementFor(phase.Phase, complete: true)));
        }
    }

    private static string AnnouncementFor(AdficationPhase phase, bool complete) => (phase, complete) switch
    {
        (AdficationPhase.SourceIntakeAndAuthorization, true) => "Source validated.",
        (AdficationPhase.AudioCharacterizationAndDialogueSegmentation, true) => "Audio analysis and transcription complete.",
        (AdficationPhase.VisualSegmentationAndKeyframes, true) => "Scene index complete.",
        (AdficationPhase.RecurringPersonContinuityAndCharacterGraph, true) => "Recurring character check complete.",
        (AdficationPhase.OcrAndVisualTextInterpretation, true) => "On-screen text check complete.",
        (AdficationPhase.VlmSceneAnalytics, true) => "Visual scene analysis complete.",
        (AdficationPhase.AdNecessityAndNarrationWindows, true) => "Narration opportunities selected.",
        (AdficationPhase.DraftGeneration, true) => "Audio description draft complete.",
        (AdficationPhase.PiperNarrationAndTimingValidation, true) => "Narration timing complete.",
        (AdficationPhase.InitialPreviewAssembly, true) => "Project ready for review.",
        (AdficationPhase.NaturalConversationReview, true) => "Review changes saved.",
        (AdficationPhase.FinalProjectValidation, true) => "Final project validation complete.",
        (AdficationPhase.FinalMp4Rendering, true) => "Final ADFied MP4 complete.",
        (AdficationPhase.OptionalPostFinalizationActions, true) => "Post-finalization action complete.",
        _ when complete => $"{phase} complete.",
        _ => $"Starting {phase}.",
    };
}
