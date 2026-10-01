namespace ADFi.Core;

public readonly record struct MediaInterval(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public bool Overlaps(MediaInterval other) => Start < other.End && other.Start < End;

    public void ValidateWithin(TimeSpan mediaDuration)
    {
        if (Start < TimeSpan.Zero || End <= Start || End > mediaDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(mediaDuration), "Interval must be positive and inside the media duration.");
        }
    }
}

public sealed record AudioChunk(
    string ChunkId,
    MediaInterval SourceInterval,
    string RelativeAudioPath,
    int SampleRate,
    int Channels);

public sealed record TranscriptWord(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    decimal? Confidence);

public sealed record TranscriptSegment(
    string SegmentId,
    string ChunkId,
    MediaInterval SourceInterval,
    string Text,
    decimal? Confidence,
    IReadOnlyList<TranscriptWord> Words);

public enum SourceAudioKind
{
    Dialogue,
    MeaningfulNonDialogue,
    QuietCandidate,
}

public sealed record SourceAudioInterval(
    MediaInterval Interval,
    SourceAudioKind Kind,
    decimal Confidence,
    string EvidenceId);

public sealed record KeyframeEvidence(
    string EvidenceId,
    TimeSpan SourceTime,
    string RelativeImagePath,
    int Width,
    int Height,
    decimal SimilarityToPrevious,
    decimal MotionScore,
    bool PotentialText);

public sealed record VisualScene(
    string SceneId,
    MediaInterval SourceInterval,
    IReadOnlyList<KeyframeEvidence> Keyframes,
    IReadOnlyList<string> TranscriptSegmentIds,
    IReadOnlyList<string> CandidateNarrationWindowIds);

public sealed record ProposedAdInterval(
    string IntervalId,
    string SceneId,
    MediaInterval Window,
    int AvailableMilliseconds,
    IReadOnlyList<string> VisualEvidenceIds,
    IReadOnlyList<string> NearbyTranscriptSegmentIds,
    string Reason,
    decimal Importance,
    decimal Certainty);

