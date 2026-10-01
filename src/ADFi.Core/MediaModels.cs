namespace ADFi.Core;

public sealed record MediaStreamMetadata(
    string CodecType,
    string CodecName,
    int? Width,
    int? Height,
    decimal? FrameRate,
    int? SampleRate,
    int? Channels,
    long? BitRate);

public sealed record MediaProbeResult(
    string ContainerName,
    TimeSpan Duration,
    long FileSizeBytes,
    bool IsVariableFrameRate,
    IReadOnlyList<MediaStreamMetadata> Streams);

public sealed record ImmutableSourceMetadata(
    string SourcePath,
    string SourceSha256,
    long SourceSizeBytes,
    DateTimeOffset SourceLastWriteUtc,
    MediaProbeResult Probe,
    long EstimatedTemporaryBytes);

