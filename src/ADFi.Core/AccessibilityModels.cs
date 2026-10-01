namespace ADFi.Core;

public enum OcrEvidenceSource
{
    VlmContext,
    TesseractLiteral,
    UserConfirmation,
}

public sealed record OcrCandidate(
    string EvidenceId,
    OcrEvidenceSource Source,
    string Text,
    decimal Confidence,
    bool ExactCharactersRequired);

public sealed record ReconciledOcr(
    string? SupportedText,
    decimal Confidence,
    bool RequiresAdditionalEvidence,
    IReadOnlyList<OcrCandidate> CompetingReadings,
    IReadOnlyList<string> EvidenceIds);

public sealed record VisualFact(
    string FactId,
    string SceneId,
    string ObjectiveDescription,
    decimal Importance,
    decimal Certainty,
    bool AlreadyConveyedBySourceAudio,
    TimeSpan AvailableFrom,
    TimeSpan? SpoilerSafeAfter,
    IReadOnlyList<string> EvidenceIds);

public sealed record AdSegment(
    string SegmentId,
    string SceneId,
    MediaInterval Window,
    string Text,
    IReadOnlyList<string> EvidenceIds,
    string PromptId,
    string PromptVersion,
    string PromptSha256,
    string? NarrationAssetRelativePath,
    TimeSpan? MeasuredNarrationDuration,
    bool UserApproved);

public sealed record ProjectValidationIssue(
    string Code,
    string Message,
    bool Blocking,
    string? SegmentId);

