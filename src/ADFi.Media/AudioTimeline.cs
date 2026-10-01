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

public static class AudioTimelineComposer
{
    public static IReadOnlyList<SourceAudioInterval> Combine(
        TimeSpan mediaDuration,
        IReadOnlyList<SourceAudioInterval> signalTimeline,
        IReadOnlyList<TranscriptSegment> transcript,
        TimeSpan dialoguePadding)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(mediaDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(dialoguePadding, TimeSpan.Zero);
        var dialogue = transcript
            .Select(segment => new MediaInterval(
                Max(TimeSpan.Zero, segment.SourceInterval.Start - dialoguePadding),
                Min(mediaDuration, segment.SourceInterval.End + dialoguePadding)))
            .ToArray();
        var boundaries = new SortedSet<TimeSpan> { TimeSpan.Zero, mediaDuration };
        foreach (var interval in signalTimeline.Select(item => item.Interval).Concat(dialogue))
        {
            interval.ValidateWithin(mediaDuration);
            boundaries.Add(interval.Start);
            boundaries.Add(interval.End);
        }

        var points = boundaries.ToArray();
        var result = new List<SourceAudioInterval>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var interval = new MediaInterval(points[index], points[index + 1]);
            if (interval.Duration <= TimeSpan.Zero)
            {
                continue;
            }

            var kind = dialogue.Any(candidate => candidate.Overlaps(interval))
                ? SourceAudioKind.Dialogue
                : signalTimeline.Any(item => item.Kind == SourceAudioKind.QuietCandidate && item.Interval.Overlaps(interval))
                    ? SourceAudioKind.QuietCandidate
                    : SourceAudioKind.MeaningfulNonDialogue;
            AppendOrMerge(result, new(
                interval,
                kind,
                kind == SourceAudioKind.Dialogue ? 1m : 0.8m,
                $"audio-combined-{index:D5}"));
        }

        return result;
    }

    private static void AppendOrMerge(List<SourceAudioInterval> result, SourceAudioInterval next)
    {
        if (result.Count > 0 && result[^1].Kind == next.Kind && result[^1].Interval.End == next.Interval.Start)
        {
            var prior = result[^1];
            result[^1] = prior with { Interval = new(prior.Interval.Start, next.Interval.End) };
            return;
        }

        result.Add(next);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}
