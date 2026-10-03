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
        var rejectedLowInformationIds = FindRepeatedSingleCharacterArtifacts(ordered);
        var merged = new List<TranscriptSegment>(ordered.Length);
        foreach (var candidate in ordered)
        {
            candidate.SourceInterval.ValidateWithin(mediaDuration);
            if (rejectedLowInformationIds.Contains(candidate.SegmentId))
            {
                continue;
            }

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

    private static HashSet<string> FindRepeatedSingleCharacterArtifacts(
        TranscriptSegment[] ordered)
    {
        // Scores and tonal sound effects can make Whisper emit a sustained run of the same
        // isolated letter (for example "A" every few hundred milliseconds). Treating those
        // fragments as dialogue blocks valid title-card and opening-description windows. Keep
        // isolated one-letter utterances, but suppress a dense repeated run that contains no
        // lexical information. This is deliberately narrow so short real dialogue is retained.
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Length;)
        {
            var token = SingleLetterToken(ordered[index].Text);
            if (token is null)
            {
                index++;
                continue;
            }

            var end = index + 1;
            while (end < ordered.Length
                   && string.Equals(SingleLetterToken(ordered[end].Text), token, StringComparison.OrdinalIgnoreCase)
                   && ordered[end].SourceInterval.Start - ordered[end - 1].SourceInterval.End <= TimeSpan.FromSeconds(1))
            {
                end++;
            }

            var runDuration = ordered[end - 1].SourceInterval.End - ordered[index].SourceInterval.Start;
            if (end - index >= 3 && runDuration >= TimeSpan.FromSeconds(1.5))
            {
                for (var rejectedIndex = index; rejectedIndex < end; rejectedIndex++)
                {
                    rejected.Add(ordered[rejectedIndex].SegmentId);
                }
            }

            index = end;
        }

        return rejected;
    }

    private static string? SingleLetterToken(string value)
    {
        var letters = value.Where(char.IsLetterOrDigit).ToArray();
        return letters.Length == 1 ? new string(letters) : null;
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

    public static IReadOnlyList<MediaInterval> FindDuckedSoundWindows(
        TimeSpan mediaDuration,
        IEnumerable<SourceAudioInterval> audioTimeline,
        TimeSpan minimumWindow,
        TimeSpan breathingMargin)
    {
        if (minimumWindow <= TimeSpan.Zero || breathingMargin < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumWindow));
        }

        // Music and effects can carry important information, but treating every audible sample as
        // an absolute narration prohibition leaves nonverbal films inaccessible. Merge adjacent
        // quiet and meaningful-sound classifications into continuous dialogue-free ranges before
        // applying margins; otherwise millisecond energy-classification changes can fragment an
        // opening score and suppress a title description. These windows remain secondary to quiet
        // windows and are never derived from dialogue intervals.
        var dialogueFree = new List<MediaInterval>();
        foreach (var item in audioTimeline
                     .Where(item => item.Kind != SourceAudioKind.Dialogue)
                     .OrderBy(item => item.Interval.Start))
        {
            if (dialogueFree.Count > 0 && item.Interval.Start <= dialogueFree[^1].End)
            {
                var prior = dialogueFree[^1];
                dialogueFree[^1] = new(prior.Start, item.Interval.End > prior.End ? item.Interval.End : prior.End);
            }
            else
            {
                dialogueFree.Add(item.Interval);
            }
        }

        var windows = dialogueFree
            .Select(interval => new MediaInterval(
                interval.Start + breathingMargin,
                interval.End - breathingMargin))
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
