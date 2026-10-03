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
    public void AdOpportunityNeverStartsBeforeItsLatestCitedVisualFact()
    {
        var scene = new VisualScene(
            "scene-1",
            new(TimeSpan.Zero, TimeSpan.FromSeconds(8)),
            [new("frame-1", TimeSpan.FromSeconds(3), "frames/1.jpg", 640, 360, 0, 0, false)],
            [],
            []);
        var fact = new VisualFact(
            "fact-1", "scene-1", "A hand places a key on the table.", 0.9m, 0.95m, false,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), ["frame-1"]);

        var proposed = AdNecessityPolicy.Propose(
            [scene],
            [fact],
            [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7))],
            TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(proposed).Window.Start);
    }

    [Fact]
    public void ImportantOpeningVisualUsesDuckedSoundBedWhenNoQuietWindowExists()
    {
        var scene = new VisualScene(
            "scene-opening",
            new(TimeSpan.Zero, TimeSpan.FromSeconds(12)),
            [new("frame-opening", TimeSpan.FromSeconds(1), "frames/opening.jpg", 640, 360, 0, 0, false)],
            [],
            []);
        var fact = new VisualFact(
            "fact-opening", "scene-opening", "A rabbit emerges into a meadow.", 0.95m, 0.95m,
            false, TimeSpan.FromSeconds(1), null, ["frame-opening"]);

        var proposed = AdNecessityPolicy.Propose(
            [scene],
            [fact],
            [],
            TimeSpan.FromSeconds(2),
            [new(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(11.75))]);

        var interval = Assert.Single(proposed);
        Assert.Equal(TimeSpan.FromSeconds(1), interval.Window.Start);
        Assert.Equal("scene-opening", interval.SceneId);
    }

    [Fact]
    public void QuietWindowIsPreferredAndDuckedFallbackIsSpaced()
    {
        var scenes = Enumerable.Range(0, 3)
            .Select(index => new VisualScene(
                $"scene-{index}",
                new(TimeSpan.FromSeconds(index * 10), TimeSpan.FromSeconds((index + 1) * 10)),
                [new(
                    $"frame-{index}",
                    TimeSpan.FromSeconds(index * 10 + 1),
                    $"frames/{index}.jpg",
                    640,
                    360,
                    0,
                    0,
                    false)],
                [],
                []))
            .ToArray();
        var facts = scenes.Select((scene, index) => new VisualFact(
            $"fact-{index}", scene.SceneId, $"Visual change {index}.", 0.95m, 0.95m, false,
            scene.Keyframes[0].SourceTime, null, [scene.Keyframes[0].EvidenceId])).ToArray();

        var proposed = AdNecessityPolicy.Propose(
            scenes,
            facts,
            [new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(19))],
            TimeSpan.FromSeconds(2),
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(25)),
            ],
            TimeSpan.FromSeconds(8));

        Assert.Equal(2, proposed.Count);
        Assert.Equal("scene-0", proposed[0].SceneId);
        Assert.Equal("scene-1", proposed[1].SceneId);
        Assert.DoesNotContain(proposed, item => item.SceneId == "scene-2");
    }

    [Fact]
    public void OpportunityIsSuppressedWhenOnlyWindowStartsAfterVisualEvidenceExpires()
    {
        var scene = new VisualScene(
            "scene-stale",
            new(TimeSpan.Zero, TimeSpan.FromSeconds(15)),
            [new("frame-stale", TimeSpan.FromSeconds(1), "frames/stale.jpg", 640, 360, 0, 0, false)],
            [],
            []);
        var fact = new VisualFact(
            "fact-stale",
            "scene-stale",
            "A sheep crosses the field.",
            0.9m,
            0.95m,
            false,
            TimeSpan.FromSeconds(1),
            null,
            ["frame-stale"]);

        var result = AdNecessityPolicy.Propose(
            [scene],
            [fact],
            [],
            TimeSpan.FromSeconds(2),
            [new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15))]);

        Assert.Empty(result);
    }

    [Fact]
    public void EarlyCurrentFactCanBeDescribedWithoutWaitingForLaterSceneEvidence()
    {
        var scene = new VisualScene(
            "scene-sequence",
            new(TimeSpan.Zero, TimeSpan.FromSeconds(15)),
            [
                new("frame-early", TimeSpan.FromSeconds(1), "frames/early.jpg", 640, 360, 0, 0, false),
                new("frame-late", TimeSpan.FromSeconds(13), "frames/late.jpg", 640, 360, 0, 0, false),
            ],
            [],
            []);
        var facts = new[]
        {
            new VisualFact(
                "fact-early", "scene-sequence", "A sheep steps onto a boulder.", 0.9m, 0.95m,
                false, TimeSpan.FromSeconds(1), null, ["frame-early"]),
            new VisualFact(
                "fact-late", "scene-sequence", "The sheep reaches the far hill.", 0.9m, 0.95m,
                false, TimeSpan.FromSeconds(13), null, ["frame-late"]),
        };

        var result = AdNecessityPolicy.Propose(
            [scene],
            facts,
            [],
            TimeSpan.FromSeconds(2),
            [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15))]);

        var opportunity = Assert.Single(result);
        Assert.Equal(TimeSpan.FromSeconds(1), opportunity.Window.Start);
        Assert.Contains("steps onto a boulder", opportunity.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("far hill", opportunity.Reason, StringComparison.Ordinal);
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
