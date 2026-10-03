using ADFi.Core;

namespace ADFi.Media;

public static class AdNecessityPolicy
{
    public const string Version = "2.6.0-continuous-dialogue-free-fallback";
    public static readonly TimeSpan EvidenceValidityHorizon = TimeSpan.FromSeconds(5);
    public const decimal MinimumImportance = 0.60m;
    public const decimal MinimumCertainty = 0.70m;

    public static IReadOnlyList<ProposedAdInterval> Propose(
        IReadOnlyList<VisualScene> scenes,
        IReadOnlyList<VisualFact> facts,
        IReadOnlyList<MediaInterval> quietWindows,
        TimeSpan minimumWindow,
        IReadOnlyList<MediaInterval>? duckableSoundWindows = null,
        TimeSpan? minimumDuckedSpacing = null)
    {
        var proposed = new List<ProposedAdInterval>();
        var duckedSpacing = minimumDuckedSpacing ?? TimeSpan.FromSeconds(8);
        if (duckedSpacing < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDuckedSpacing));
        }

        foreach (var scene in scenes.OrderBy(scene => scene.SourceInterval.Start))
        {
            var essentialFacts = facts
                .Where(fact => fact.SceneId == scene.SceneId)
                .Where(fact => !fact.AlreadyConveyedBySourceAudio)
                .Where(fact => fact.Importance >= MinimumImportance && fact.Certainty >= MinimumCertainty)
                .Where(fact => fact.SpoilerSafeAfter is null || fact.SpoilerSafeAfter < scene.SourceInterval.End)
                .OrderBy(FactAvailableAt)
                .ToArray();
            if (essentialFacts.Length == 0)
            {
                continue;
            }

            // Select only facts established and still visually current at the narration start.
            // Waiting for every observation in a temporal sequence delayed narration until the
            // final frame and could then describe an earlier image after the picture had changed.
            var selection = SelectWindowAndFacts(
                scene,
                essentialFacts,
                quietWindows,
                minimumWindow,
                TimeSpan.Zero);

            if (selection is null && duckableSoundWindows is not null)
            {
                var earliestAllowed = proposed.Count == 0
                    ? TimeSpan.Zero
                    : proposed[^1].Window.End + duckedSpacing;
                selection = SelectWindowAndFacts(
                    scene,
                    essentialFacts,
                    duckableSoundWindows,
                    minimumWindow,
                    earliestAllowed);
            }

            if (selection is null)
            {
                continue;
            }

            var (window, selectedFacts) = selection.Value;

            proposed.Add(new(
                $"ad-opportunity-{proposed.Count:D5}",
                scene.SceneId,
                window,
                checked((int)window.Duration.TotalMilliseconds),
                selectedFacts.SelectMany(fact => fact.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray(),
                scene.TranscriptSegmentIds,
                string.Join("; ", selectedFacts.Select(fact => fact.ObjectiveDescription)),
                selectedFacts.Max(fact => fact.Importance),
                selectedFacts.Min(fact => fact.Certainty)));
        }

        return proposed;
    }

    private static MediaInterval Intersect(MediaInterval left, MediaInterval right) =>
        new(left.Start > right.Start ? left.Start : right.Start, left.End < right.End ? left.End : right.End);

    private static (MediaInterval Window, VisualFact[] Facts)? SelectWindowAndFacts(
        VisualScene scene,
        IReadOnlyList<VisualFact> facts,
        IEnumerable<MediaInterval> sourceWindows,
        TimeSpan minimumWindow,
        TimeSpan earliestAllowed)
    {
        foreach (var sourceWindow in sourceWindows
                     .Where(candidate => candidate.Overlaps(scene.SourceInterval))
                     .Select(candidate => Intersect(candidate, scene.SourceInterval))
                     .OrderBy(candidate => candidate.Start)
                     .ThenByDescending(candidate => candidate.Duration))
        {
            foreach (var anchor in facts)
            {
                var start = Max(Max(sourceWindow.Start, earliestAllowed), FactAvailableAt(anchor));
                var end = Min(sourceWindow.End, anchor.AvailableFrom + EvidenceValidityHorizon);
                if (end - start < minimumWindow)
                {
                    continue;
                }

                var currentFacts = facts
                    .Where(fact => FactAvailableAt(fact) <= start)
                    .Where(fact => start < fact.AvailableFrom + EvidenceValidityHorizon)
                    .ToArray();
                if (currentFacts.Length > 0)
                {
                    return (new(start, end), currentFacts);
                }
            }
        }

        return null;
    }

    private static TimeSpan FactAvailableAt(VisualFact fact) =>
        fact.SpoilerSafeAfter is { } spoilerSafeAfter && spoilerSafeAfter > fact.AvailableFrom
            ? spoilerSafeAfter
            : fact.AvailableFrom;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
