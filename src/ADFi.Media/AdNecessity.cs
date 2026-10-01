using ADFi.Core;

namespace ADFi.Media;

public static class AdNecessityPolicy
{
    public const decimal MinimumImportance = 0.60m;
    public const decimal MinimumCertainty = 0.70m;

    public static IReadOnlyList<ProposedAdInterval> Propose(
        IReadOnlyList<VisualScene> scenes,
        IReadOnlyList<VisualFact> facts,
        IReadOnlyList<MediaInterval> quietWindows,
        TimeSpan minimumWindow)
    {
        var proposed = new List<ProposedAdInterval>();
        foreach (var scene in scenes.OrderBy(scene => scene.SourceInterval.Start))
        {
            var essentialFacts = facts
                .Where(fact => fact.SceneId == scene.SceneId)
                .Where(fact => !fact.AlreadyConveyedBySourceAudio)
                .Where(fact => fact.Importance >= MinimumImportance && fact.Certainty >= MinimumCertainty)
                .Where(fact => fact.SpoilerSafeAfter is null || fact.SpoilerSafeAfter <= scene.SourceInterval.Start)
                .ToArray();
            if (essentialFacts.Length == 0)
            {
                continue;
            }

            var window = quietWindows
                .Where(candidate => candidate.Overlaps(scene.SourceInterval))
                .Select(candidate => Intersect(candidate, scene.SourceInterval))
                .Where(candidate => candidate.Duration >= minimumWindow)
                .OrderByDescending(candidate => candidate.Duration)
                .FirstOrDefault();
            if (window == default)
            {
                continue;
            }

            proposed.Add(new(
                $"ad-opportunity-{proposed.Count:D5}",
                scene.SceneId,
                window,
                checked((int)window.Duration.TotalMilliseconds),
                essentialFacts.SelectMany(fact => fact.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray(),
                scene.TranscriptSegmentIds,
                string.Join("; ", essentialFacts.Select(fact => fact.ObjectiveDescription)),
                essentialFacts.Max(fact => fact.Importance),
                essentialFacts.Min(fact => fact.Certainty)));
        }

        return proposed;
    }

    private static MediaInterval Intersect(MediaInterval left, MediaInterval right) =>
        new(left.Start > right.Start ? left.Start : right.Start, left.End < right.End ? left.End : right.End);
}

