using ADFi.Core;

namespace ADFi.Media;

public static class OcrReconciler
{
    public static ReconciledOcr Reconcile(IReadOnlyList<OcrCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return new(null, 0, true, [], []);
        }

        var usable = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            .OrderByDescending(candidate => candidate.Source == OcrEvidenceSource.UserConfirmation)
            .ThenByDescending(candidate => candidate.Confidence)
            .ToArray();
        if (usable.Length == 0)
        {
            return new(null, 0, true, [], []);
        }

        var groups = usable
            .GroupBy(candidate => Normalize(candidate.Text), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Max(candidate => candidate.Confidence))
            .ToArray();
        var bestGroup = groups[0].ToArray();
        var userConfirmed = usable.FirstOrDefault(candidate => candidate.Source == OcrEvidenceSource.UserConfirmation);
        if (userConfirmed is not null)
        {
            return Result(userConfirmed.Text, userConfirmed.Confidence, false, usable);
        }

        var independentAgreement = bestGroup.Select(candidate => candidate.Source).Distinct().Count() >= 2;
        if (independentAgreement)
        {
            var best = bestGroup.OrderByDescending(candidate => candidate.Confidence).First();
            return Result(best.Text, bestGroup.Average(candidate => candidate.Confidence), false, usable);
        }

        var literal = usable.FirstOrDefault(candidate =>
            candidate.Source == OcrEvidenceSource.TesseractLiteral
            && candidate.Confidence >= 0.90m);
        if (literal is not null && groups.Length == 1)
        {
            return Result(literal.Text, literal.Confidence, false, usable);
        }

        return Result(null, usable.Max(candidate => candidate.Confidence), true, usable);
    }

    private static ReconciledOcr Result(
        string? text,
        decimal confidence,
        bool needsEvidence,
        IReadOnlyList<OcrCandidate> candidates) =>
        new(
            text,
            confidence,
            needsEvidence,
            candidates,
            candidates.Select(candidate => candidate.EvidenceId).Distinct(StringComparer.Ordinal).ToArray());

    private static string Normalize(string text) =>
        string.Join(' ', text.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries)).Trim();
}

