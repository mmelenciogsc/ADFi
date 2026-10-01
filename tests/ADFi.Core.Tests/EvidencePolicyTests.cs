using ADFi.Core;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class EvidencePolicyTests
{
    [Fact]
    public void OcrDisagreementRemainsUncertainInsteadOfInventingText()
    {
        var result = OcrReconciler.Reconcile(
        [
            new("vlm-1", OcrEvidenceSource.VlmContext, "Gate 12", 0.81m, true),
            new("ocr-1", OcrEvidenceSource.TesseractLiteral, "Gate 17", 0.82m, true),
        ]);

        Assert.Null(result.SupportedText);
        Assert.True(result.RequiresAdditionalEvidence);
        Assert.Equal(2, result.CompetingReadings.Count);
    }

    [Fact]
    public void IndependentOcrAgreementPreservesExactBestReading()
    {
        var result = OcrReconciler.Reconcile(
        [
            new("vlm-1", OcrEvidenceSource.VlmContext, "Platform 9", 0.84m, true),
            new("ocr-1", OcrEvidenceSource.TesseractLiteral, "Platform 9", 0.94m, true),
        ]);

        Assert.Equal("Platform 9", result.SupportedText);
        Assert.False(result.RequiresAdditionalEvidence);
    }

    [Fact]
    public void AdOpportunityRequiresImportantCertainNonredundantFactAndQuietWindow()
    {
        var scene = new VisualScene(
            "scene-1",
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)),
            [new("frame-1", TimeSpan.FromSeconds(15), "frames/1.jpg", 640, 360, 0, 0, false)],
            ["transcript-1"],
            []);
        var facts = new[]
        {
            new VisualFact("fact-1", "scene-1", "John pockets the key.", 0.9m, 0.95m, false, TimeSpan.FromSeconds(12), null, ["frame-1"]),
            new VisualFact("fact-2", "scene-1", "Dialogue already says the door opens.", 1m, 1m, true, TimeSpan.FromSeconds(12), null, ["frame-1"]),
        };

        var proposed = AdNecessityPolicy.Propose(
            [scene],
            facts,
            [new(TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(18))],
            TimeSpan.FromSeconds(1));

        var interval = Assert.Single(proposed);
        Assert.Equal("scene-1", interval.SceneId);
        Assert.Contains("pockets the key", interval.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("door opens", interval.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalValidationRejectsOverlapMissingEvidenceAndOverrun()
    {
        var segments = new[]
        {
            Segment("one", 1, 3, "First description.", ["frame-1"], "narration/one.wav", 2.2),
            Segment("two", 2.5, 4, "Second description.", ["missing-frame"], null, 1),
        };

        var issues = ProjectValidator.Validate(
            TimeSpan.FromSeconds(10),
            segments,
            new HashSet<string>(["frame-1"], StringComparer.Ordinal),
            new HashSet<string>(["narration/one.wav"], StringComparer.Ordinal));

        Assert.Contains(issues, issue => issue.Code == "NARRATION_OVERRUN");
        Assert.Contains(issues, issue => issue.Code == "NARRATION_OVERLAP");
        Assert.Contains(issues, issue => issue.Code == "EVIDENCE_MISSING");
        Assert.Contains(issues, issue => issue.Code == "NARRATION_MISSING");
    }

    private static AdSegment Segment(
        string id,
        double start,
        double end,
        string text,
        IReadOnlyList<string> evidence,
        string? narration,
        double renderedSeconds) =>
        new(
            id,
            "scene-1",
            new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)),
            text,
            evidence,
            "ADFI_AD_WRITER_SYSTEM",
            "1.0.0",
            new string('A', 64),
            narration,
            TimeSpan.FromSeconds(renderedSeconds),
            false);
}

