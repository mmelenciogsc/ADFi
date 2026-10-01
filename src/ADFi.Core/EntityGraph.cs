namespace ADFi.Core;

public enum EntityKind
{
    Person,
    InanimateObject,
    Location,
}

public enum IdentityEvidenceKind
{
    TranscriptDialogue,
    OnScreenLabel,
    Credit,
    ProjectMetadata,
    UserConfirmation,
}

public sealed record IdentityEvidence(
    IdentityEvidenceKind Kind,
    string SourceId,
    TimeSpan EstablishedAt,
    string Value,
    decimal Confidence);

public sealed record ProjectEntity(
    string EntityId,
    EntityKind Kind,
    string AnonymousLabel,
    IReadOnlyList<IdentityEvidence> IdentityEvidence)
{
    public string DisplayNameAt(TimeSpan sourceTime)
    {
        return IdentityEvidence
            .Where(evidence => evidence.EstablishedAt <= sourceTime)
            .OrderByDescending(evidence => evidence.Confidence)
            .ThenBy(evidence => evidence.EstablishedAt)
            .Select(evidence => evidence.Value)
            .FirstOrDefault() ?? AnonymousLabel;
    }
}
