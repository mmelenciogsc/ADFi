using ADFi.Core;

namespace ADFi.Media;

public static class AudioChunkPlanner
{
    public static IReadOnlyList<AudioChunk> Plan(
        TimeSpan duration,
        TimeSpan maximumChunkDuration,
        TimeSpan overlap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        if (maximumChunkDuration <= TimeSpan.Zero || overlap < TimeSpan.Zero || overlap >= maximumChunkDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChunkDuration));
        }

        var chunks = new List<AudioChunk>();
        var cursor = TimeSpan.Zero;
        var index = 0;
        while (cursor < duration)
        {
            var end = Min(cursor + maximumChunkDuration, duration);
            chunks.Add(new(
                $"audio-{index:D5}",
                new(cursor, end),
                $"audio/chunks/audio-{index:D5}.wav",
                16_000,
                1));
            index++;
            if (end == duration)
            {
                break;
            }

            cursor = end - overlap;
        }

        return chunks;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}

public static class TranscriptMerger
{
    private static readonly TimeSpan DuplicateTolerance = TimeSpan.FromMilliseconds(350);

    public static IReadOnlyList<TranscriptSegment> Merge(
        IEnumerable<TranscriptSegment> chunkSegments,
        TimeSpan mediaDuration)
    {
        var ordered = chunkSegments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.SourceInterval.Start)
            .ThenBy(segment => segment.SourceInterval.End)
            .ToArray();
        var merged = new List<TranscriptSegment>(ordered.Length);
        foreach (var candidate in ordered)
        {
            candidate.SourceInterval.ValidateWithin(mediaDuration);
            var normalizedText = Normalize(candidate.Text);
            var duplicate = merged.LastOrDefault(existing =>
                Math.Abs((existing.SourceInterval.Start - candidate.SourceInterval.Start).TotalMilliseconds)
                    <= DuplicateTolerance.TotalMilliseconds
                && string.Equals(Normalize(existing.Text), normalizedText, StringComparison.OrdinalIgnoreCase));
            if (duplicate is null)
            {
                merged.Add(candidate with { Text = candidate.Text.Trim() });
            }
        }

        return merged;
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
}

public static class NarrationWindowPlanner
{
    public static IReadOnlyList<MediaInterval> FindQuietWindows(
        TimeSpan mediaDuration,
        IEnumerable<SourceAudioInterval> audioTimeline,
        TimeSpan minimumWindow,
        TimeSpan breathingMargin)
    {
        if (minimumWindow <= TimeSpan.Zero || breathingMargin < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumWindow));
        }

        var windows = audioTimeline
            .Where(item => item.Kind == SourceAudioKind.QuietCandidate)
            .Select(item => new MediaInterval(
                item.Interval.Start + breathingMargin,
                item.Interval.End - breathingMargin))
            .Where(interval => interval.Duration >= minimumWindow)
            .ToArray();

        foreach (var window in windows)
        {
            window.ValidateWithin(mediaDuration);
        }

        return windows;
    }
}
