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
    public void TranscriptMergeSuppressesRepeatedSingleLetterMusicHallucinations()
    {
        var segments = new[]
        {
            Segment("noise-1", "chunk-1", 3.5, 4.5, "A"),
            Segment("noise-2", "chunk-1", 4.6, 5.2, "A"),
            Segment("noise-3", "chunk-1", 5.3, 6.1, "A"),
            Segment("real", "chunk-1", 8, 9, "Come here."),
            Segment("isolated", "chunk-1", 12, 13, "I"),
        };

        var merged = TranscriptMerger.Merge(segments, TimeSpan.FromMinutes(1));

        Assert.Equal(["real", "isolated"], merged.Select(item => item.SegmentId));
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
    public void DuckedSoundWindowsMergeAllDialogueFreeAudioButNeverDialogue()
    {
        var timeline = new[]
        {
            Audio(SourceAudioKind.Dialogue, 0, 4),
            Audio(SourceAudioKind.MeaningfulNonDialogue, 4, 10),
            Audio(SourceAudioKind.QuietCandidate, 10, 14),
        };

        var windows = NarrationWindowPlanner.FindDuckedSoundWindows(
            TimeSpan.FromSeconds(14),
            timeline,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));

        var window = Assert.Single(windows);
        Assert.Equal(TimeSpan.FromSeconds(4.25), window.Start);
        Assert.Equal(TimeSpan.FromSeconds(13.75), window.End);
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
    public void SalientCandidatesDoNotDisplacePlannedTemporalCoverage()
    {
        var scene = new MediaInterval(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(25));
        var candidates = Enumerable.Range(0, 12)
            .Select(index => new FrameCandidate(
                TimeSpan.FromSeconds(10 + index * 0.5),
                0.9m,
                0.9m,
                0.9m,
                true))
            .ToArray();

        var times = KeyframeSequencePlanner.SelectTimes(scene, 3, candidates);

        Assert.Equal(
            [TimeSpan.FromSeconds(11.5), TimeSpan.FromSeconds(17.5), TimeSpan.FromSeconds(23.5)],
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

    [Fact]
    public void SpeechEstimateUsesConfiguredPlanningRate()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 18));
        Assert.Equal(
            TimeSpan.FromSeconds(6),
            NarrationTimingPolicy.EstimateSpeechDuration(text));
    }

    [Fact]
    public void TranscriptOverridesSignalSilenceAndPreservesMeaningfulSound()
    {
        var signal = new[]
        {
            Audio(SourceAudioKind.MeaningfulNonDialogue, 0, 2),
            Audio(SourceAudioKind.QuietCandidate, 2, 8),
            Audio(SourceAudioKind.MeaningfulNonDialogue, 8, 10),
        };
        var transcript = new[] { Segment("speech", "chunk-1", 3, 5, "He speaks.") };

        var combined = AudioTimelineComposer.Combine(
            TimeSpan.FromSeconds(10),
            signal,
            transcript,
            TimeSpan.FromMilliseconds(250));

        Assert.Equal(
            [SourceAudioKind.MeaningfulNonDialogue, SourceAudioKind.QuietCandidate, SourceAudioKind.Dialogue, SourceAudioKind.QuietCandidate, SourceAudioKind.MeaningfulNonDialogue],
            combined.Select(item => item.Kind));
        Assert.Equal(TimeSpan.FromSeconds(2.75), combined[2].Interval.Start);
        Assert.Equal(TimeSpan.FromSeconds(5.25), combined[2].Interval.End);
    }

    private static TranscriptSegment Segment(string id, string chunk, double start, double end, string text) =>
        new(id, chunk, new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)), text, 0.9m, []);

    private static SourceAudioInterval Audio(SourceAudioKind kind, double start, double end) =>
        new(new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)), kind, 0.9m, $"audio-{start}");
}
