using ADFi.Core;

namespace ADFi.Media;

public static class KeyframeSequencePlanner
{
    public static IReadOnlyList<TimeSpan> SelectTimes(MediaInterval scene, int maximumFrames)
    {
        if (maximumFrames is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        }

        if (scene.Start < TimeSpan.Zero || scene.End <= scene.Start)
        {
            throw new ArgumentOutOfRangeException(nameof(scene));
        }

        if (maximumFrames == 1 || scene.Duration < TimeSpan.FromSeconds(1))
        {
            return [scene.Start + scene.Duration / 2];
        }

        var count = Math.Min(maximumFrames, 3);
        if (count == 2)
        {
            return [scene.Start + scene.Duration * 0.25, scene.Start + scene.Duration * 0.75];
        }

        return
        [
            scene.Start + scene.Duration * 0.10,
            scene.Start + scene.Duration * 0.50,
            scene.Start + scene.Duration * 0.90,
        ];
    }
}

public sealed record FrameCandidate(
    TimeSpan SourceTime,
    decimal StructuralDifference,
    decimal ColorDifference,
    decimal MotionScore,
    bool PotentialText);

public static class ShotBoundaryPolicy
{
    public static IReadOnlyList<TimeSpan> Detect(
        IEnumerable<FrameCandidate> candidates,
        decimal cutThreshold = 0.55m)
    {
        if (cutThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cutThreshold));
        }

        return candidates
            .OrderBy(candidate => candidate.SourceTime)
            .Where(candidate =>
                WeightedDifference(candidate) >= cutThreshold
                || candidate.PotentialText && candidate.StructuralDifference >= cutThreshold * 0.75m)
            .Select(candidate => candidate.SourceTime)
            .Distinct()
            .ToArray();
    }

    private static decimal WeightedDifference(FrameCandidate candidate) =>
        candidate.StructuralDifference * 0.50m
        + candidate.ColorDifference * 0.20m
        + candidate.MotionScore * 0.30m;
}

