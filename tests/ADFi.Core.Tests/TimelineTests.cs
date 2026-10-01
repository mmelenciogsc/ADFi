using ADFi.Core;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class TimelineTests
{
    [Fact]
    public void LongAudioIsChunkedWithBoundedOverlap()
    {
        var chunks = AudioChunkPlanner.Plan(
            TimeSpan.FromSeconds(65),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1));

        Assert.Equal(3, chunks.Count);
        Assert.Equal(TimeSpan.Zero, chunks[0].SourceInterval.Start);
        Assert.Equal(TimeSpan.FromSeconds(30), chunks[0].SourceInterval.End);
        Assert.Equal(TimeSpan.FromSeconds(29), chunks[1].SourceInterval.Start);
        Assert.Equal(TimeSpan.FromSeconds(58), chunks[2].SourceInterval.Start);
        Assert.Equal(TimeSpan.FromSeconds(65), chunks[2].SourceInterval.End);
        Assert.All(chunks, chunk => Assert.True(chunk.SourceInterval.Duration <= TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void TranscriptMergeRemovesOverlapDuplicatesDeterministically()
    {
        var duplicateText = "The door opens.";
        var segments = new[]
        {
            Segment("one", "chunk-1", 10, 11, duplicateText),
            Segment("two", "chunk-2", 10.2, 11.2, "  The door  opens. "),
            Segment("three", "chunk-2", 12, 13, "John enters."),
        };

        var merged = TranscriptMerger.Merge(segments, TimeSpan.FromMinutes(1));

        Assert.Equal(2, merged.Count);
        Assert.Equal(["one", "three"], merged.Select(item => item.SegmentId));
    }

    [Fact]
    public void NarrationWindowsExcludeMeaningfulSourceSoundAndKeepBreathingRoom()
    {
        var timeline = new[]
        {
            Audio(SourceAudioKind.Dialogue, 0, 3),
            Audio(SourceAudioKind.QuietCandidate, 3, 8),
            Audio(SourceAudioKind.MeaningfulNonDialogue, 8, 12),
            Audio(SourceAudioKind.QuietCandidate, 12, 13),
        };

        var windows = NarrationWindowPlanner.FindQuietWindows(
            TimeSpan.FromSeconds(13),
            timeline,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(300));

        var window = Assert.Single(windows);
        Assert.Equal(TimeSpan.FromSeconds(3.3), window.Start);
        Assert.Equal(TimeSpan.FromSeconds(7.7), window.End);
    }

    [Fact]
    public void KeyframeSequenceIsBoundedAndTemporal()
    {
        var times = KeyframeSequencePlanner.SelectTimes(
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)),
            maximumFrames: 5);

        Assert.Equal(
            [TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(19)],
            times);
    }

    [Fact]
    public void FinalFailedAutoFitAttemptRequiresReview()
    {
        var result = NarrationTimingPolicy.Evaluate(
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(250),
            NarrationTimingPolicy.MaximumAutoFitAttempts);

        Assert.False(result.Fits);
        Assert.True(result.RequiresUserReview);
    }

    private static TranscriptSegment Segment(string id, string chunk, double start, double end, string text) =>
        new(id, chunk, new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)), text, 0.9m, []);

    private static SourceAudioInterval Audio(SourceAudioKind kind, double start, double end) =>
        new(new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)), kind, 0.9m, $"audio-{start}");
}
