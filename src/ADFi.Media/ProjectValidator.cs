using ADFi.Core;

namespace ADFi.Media;

public static class ProjectValidator
{
    public static IReadOnlyList<ProjectValidationIssue> Validate(
        TimeSpan mediaDuration,
        IReadOnlyList<AdSegment> segments,
        ISet<string> availableEvidenceIds,
        ISet<string> availableNarrationAssets)
    {
        var issues = new List<ProjectValidationIssue>();
        AdSegment? previous = null;
        foreach (var segment in segments.OrderBy(segment => segment.Window.Start))
        {
            try
            {
                segment.Window.ValidateWithin(mediaDuration);
            }
            catch (ArgumentOutOfRangeException)
            {
                issues.Add(new("SEGMENT_OUTSIDE_MEDIA", "Description timing falls outside the source media.", true, segment.SegmentId));
            }

            if (previous is not null && previous.Window.End > segment.Window.Start)
            {
                issues.Add(new("NARRATION_OVERLAP", "Description segments overlap.", true, segment.SegmentId));
            }

            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                issues.Add(new("DESCRIPTION_EMPTY", "Description text is empty.", true, segment.SegmentId));
            }

            if (segment.EvidenceIds.Any(evidenceId => !availableEvidenceIds.Contains(evidenceId)))
            {
                issues.Add(new("EVIDENCE_MISSING", "Description references unavailable visual evidence.", true, segment.SegmentId));
            }

            if (segment.NarrationAssetRelativePath is null
                || !availableNarrationAssets.Contains(segment.NarrationAssetRelativePath))
            {
                issues.Add(new("NARRATION_MISSING", "Description has no validated narration asset.", true, segment.SegmentId));
            }

            if (segment.MeasuredNarrationDuration is { } rendered
                && rendered > segment.Window.Duration)
            {
                issues.Add(new("NARRATION_OVERRUN", "Rendered narration exceeds its available window.", true, segment.SegmentId));
            }

            previous = segment;
        }

        return issues;
    }
}
