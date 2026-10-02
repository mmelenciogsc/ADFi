namespace ADFi.Core;

public enum ProjectMutationKind
{
    GenerateInitialDraft,
    AddAdSegment,
    ReplaceAdSegment,
    RemoveAdSegment,
    SelectNarrationVoice,
    RegenerateNarration,
    Undo,
    RestoreCheckpoint,
}

public sealed record ProjectSnapshot(
    Guid ProjectId,
    int Version,
    string SourceSha256,
    TimeSpan MediaDuration,
    IReadOnlyList<AdSegment> AdSegments,
    string NarrationVoiceId,
    DateTimeOffset UpdatedAtUtc,
    int? RestoredFromVersion);

public sealed record ProjectMutationRecord(
    Guid MutationId,
    Guid ProjectId,
    int PreviousVersion,
    int ResultVersion,
    ProjectMutationKind Kind,
    string Reason,
    DateTimeOffset AppliedAtUtc);

public interface IProjectVersionStore
{
    Task InitializeAsync(ProjectSnapshot initial, CancellationToken cancellationToken);

    Task<ProjectSnapshot> LoadCurrentAsync(Guid projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectSnapshot>> LoadHistoryAsync(Guid projectId, CancellationToken cancellationToken);

    Task AppendAsync(
        ProjectSnapshot snapshot,
        ProjectMutationRecord mutation,
        int expectedCurrentVersion,
        CancellationToken cancellationToken);
}
