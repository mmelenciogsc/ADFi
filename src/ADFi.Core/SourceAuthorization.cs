namespace ADFi.Core;

public static class RightsAttestationPolicy
{
    public const string Version = "2026-10-02.1";

    public const string Text =
        "I confirm that I own this media or have all rights/authorization necessary to process, modify and publish this ADFied version.";
}

public sealed record RightsAttestation(
    Guid UserId,
    Guid ProjectId,
    DateTimeOffset AttestedAtUtc,
    string AttestationVersion,
    string AttestationText,
    string SourceSha256,
    bool Accepted,
    string? YouTubeChannelId = null,
    string? YouTubePublicationId = null)
{
    public static RightsAttestation Create(
        Guid userId,
        Guid projectId,
        DateTimeOffset attestedAtUtc,
        string sourceSha256,
        bool accepted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);

        if (!accepted)
        {
            throw new InvalidOperationException("Rights authorization must be accepted before source processing.");
        }

        return new(
            userId,
            projectId,
            attestedAtUtc.ToUniversalTime(),
            RightsAttestationPolicy.Version,
            RightsAttestationPolicy.Text,
            sourceSha256.ToUpperInvariant(),
            true);
    }
}

