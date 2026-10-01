using ADFi.Core;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed class ProjectMutationService(
    IProjectVersionStore store,
    TimeProvider timeProvider)
{
    public Task<ProjectSnapshot> ReplaceAdSegmentAsync(
        Guid projectId,
        int expectedVersion,
        AdSegment proposedSegment,
        ISet<string> availableEvidenceIds,
        ISet<string> availableNarrationAssets,
        string reason,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            projectId,
            expectedVersion,
            ProjectMutationKind.ReplaceAdSegment,
            reason,
            current =>
            {
                var segments = current.AdSegments
                    .Where(segment => segment.SegmentId != proposedSegment.SegmentId)
                    .Append(proposedSegment)
                    .OrderBy(segment => segment.Window.Start)
                    .ToArray();
                EnsureValid(current.MediaDuration, segments, availableEvidenceIds, availableNarrationAssets);
                return current with { AdSegments = segments };
            },
            cancellationToken);

    public Task<ProjectSnapshot> RemoveAdSegmentAsync(
        Guid projectId,
        int expectedVersion,
        string segmentId,
        string reason,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            projectId,
            expectedVersion,
            ProjectMutationKind.RemoveAdSegment,
            reason,
            current => current with
            {
                AdSegments = current.AdSegments.Where(segment => segment.SegmentId != segmentId).ToArray(),
            },
            cancellationToken);

    public Task<ProjectSnapshot> SelectVoiceAsync(
        Guid projectId,
        int expectedVersion,
        string voiceId,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = PiperVoiceCatalog.Get(voiceId);
        return ApplyAsync(
            projectId,
            expectedVersion,
            ProjectMutationKind.SelectNarrationVoice,
            reason,
            current => current with { NarrationVoiceId = voiceId },
            cancellationToken);
    }

    public async Task<ProjectSnapshot> UndoAsync(
        Guid projectId,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var history = await store.LoadHistoryAsync(projectId, cancellationToken).ConfigureAwait(false);
        var prior = history.SingleOrDefault(snapshot => snapshot.Version == expectedVersion - 1)
                    ?? throw new InvalidOperationException("NOTHING_TO_UNDO");
        return await ApplyAsync(
            projectId,
            expectedVersion,
            ProjectMutationKind.Undo,
            $"Undo version {expectedVersion} by restoring version {prior.Version}.",
            current => prior with { RestoredFromVersion = prior.Version },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectSnapshot> RestoreCheckpointAsync(
        Guid projectId,
        int expectedVersion,
        int restoreVersion,
        CancellationToken cancellationToken)
    {
        var history = await store.LoadHistoryAsync(projectId, cancellationToken).ConfigureAwait(false);
        var selected = history.SingleOrDefault(snapshot => snapshot.Version == restoreVersion)
                       ?? throw new InvalidOperationException("CHECKPOINT_INVALID");
        return await ApplyAsync(
            projectId,
            expectedVersion,
            ProjectMutationKind.RestoreCheckpoint,
            $"Restore checkpoint version {restoreVersion}.",
            current => selected with { RestoredFromVersion = restoreVersion },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectSnapshot> ApplyAsync(
        Guid projectId,
        int expectedVersion,
        ProjectMutationKind kind,
        string reason,
        Func<ProjectSnapshot, ProjectSnapshot> transform,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var current = await store.LoadCurrentAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (current.Version != expectedVersion)
        {
            throw new InvalidOperationException("PROJECT_VERSION_CONFLICT");
        }

        var updated = transform(current) with
        {
            ProjectId = projectId,
            Version = expectedVersion + 1,
            UpdatedAtUtc = timeProvider.GetUtcNow(),
        };
        var mutation = new ProjectMutationRecord(
            Guid.NewGuid(),
            projectId,
            expectedVersion,
            updated.Version,
            kind,
            reason,
            updated.UpdatedAtUtc);
        await store.AppendAsync(updated, mutation, expectedVersion, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static void EnsureValid(
        TimeSpan duration,
        IReadOnlyList<AdSegment> segments,
        ISet<string> evidence,
        ISet<string> narration)
    {
        var blockers = ProjectValidator.Validate(duration, segments, evidence, narration)
            .Where(issue => issue.Blocking)
            .ToArray();
        if (blockers.Length > 0)
        {
            throw new InvalidOperationException(
                $"VALIDATION_FAILED: {string.Join(", ", blockers.Select(issue => issue.Code))}");
        }
    }
}
