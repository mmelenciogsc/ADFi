using System.Text.Json;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record NarrationAutoFitResult(
    AdSegment Segment,
    NarrationFitResult Fit,
    int SynthesisAttempts,
    bool RequiresUserReview);

public sealed class NarrationAutoFitCoordinator(
    INarrationSynthesizer synthesizer,
    ILocalModelRuntime runtime,
    string modelName,
    string writerPromptPath)
{
    private const string RewriteSchema =
        """
        {"type":"object","required":["text","evidenceIds"],"properties":{"text":{"type":"string"},"evidenceIds":{"type":"array","minItems":1,"items":{"type":"string"}}},"additionalProperties":false}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<NarrationAutoFitResult> FitAsync(
        AdSegment segment,
        string voiceId,
        TimeSpan breathingRoom,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var text = segment.Text;
        NarrationSynthesisResult? synthesis = null;
        for (var attempt = 1; attempt <= NarrationTimingPolicy.MaximumAutoFitAttempts; attempt++)
        {
            synthesis = await synthesizer.SynthesizeAsync(
                new(
                    segment.SegmentId,
                    text,
                    voiceId,
                    segment.Window.Duration,
                    breathingRoom,
                    attempt),
                cancellationToken).ConfigureAwait(false);
            if (synthesis.Fit.Fits)
            {
                return BuildResult(segment, text, synthesis, attempt);
            }

            if (attempt == NarrationTimingPolicy.MaximumAutoFitAttempts)
            {
                break;
            }

            text = await ShortenAsync(segment, text, synthesis.MeasuredDuration, breathingRoom, cancellationToken)
                .ConfigureAwait(false);
        }

        return BuildResult(segment, text, synthesis!, NarrationTimingPolicy.MaximumAutoFitAttempts);
    }

    private async Task<string> ShortenAsync(
        AdSegment segment,
        string currentText,
        TimeSpan renderedDuration,
        TimeSpan breathingRoom,
        CancellationToken cancellationToken)
    {
        var prompt = await PromptResourceLoader.LoadAsync(writerPromptPath, cancellationToken).ConfigureAwait(false);
        await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
        try
        {
            var available = segment.Window.Duration - breathingRoom;
            var currentWordCount = currentText.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries).Length;
            var maximumWords = Math.Max(
                1,
                (int)Math.Floor(currentWordCount * available.TotalMilliseconds
                                / Math.Max(1, renderedDuration.TotalMilliseconds) * 0.9));
            var response = await runtime.GenerateAsync(
                modelName,
                new(
                    prompt.Content,
                    JsonSerializer.Serialize(new
                    {
                        operation = "AutoFitNarrationToWindow",
                        currentText,
                        requiredEvidenceIds = segment.EvidenceIds,
                        maximumDurationMilliseconds = checked((long)available.TotalMilliseconds),
                        renderedDurationMilliseconds = checked((long)renderedDuration.TotalMilliseconds),
                        maximumWords,
                        instruction = $"Shorten wording to no more than {maximumWords} spoken words. Preserve the single most essential supported action or state and every evidence ID. Do not add facts. The word limit is mandatory.",
                    }),
                    [],
                    RewriteSchema,
                    350),
                cancellationToken).ConfigureAwait(false);
            RewriteResponse? rewrite;
            try
            {
                rewrite = JsonSerializer.Deserialize<RewriteResponse>(response.Content, JsonOptions);
            }
            catch (JsonException)
            {
                return currentText;
            }

            if (rewrite is null
                || string.IsNullOrWhiteSpace(rewrite.Text)
                || rewrite.EvidenceIds is null
                || !segment.EvidenceIds.ToHashSet(StringComparer.Ordinal).SetEquals(rewrite.EvidenceIds))
            {
                // An invalid model rewrite is never accepted. Retaining the last verified text lets
                // the finite timing loop finish and surfaces a required human review instead of
                // aborting the entire resumable project.
                return currentText;
            }

            if (rewrite.Text.Length >= currentText.Length)
            {
                // A bounded model may be unable to produce a shorter evidence-preserving
                // sentence. Keep the current text, finish the finite measurement attempts,
                // and surface RequiresUserReview instead of aborting the whole project.
                return currentText;
            }

            return rewrite.Text.Trim();
        }
        finally
        {
            await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static NarrationAutoFitResult BuildResult(
        AdSegment original,
        string text,
        NarrationSynthesisResult synthesis,
        int attempts)
    {
        var updated = original with
        {
            Text = text,
            NarrationAssetRelativePath = synthesis.AudioPath,
            MeasuredNarrationDuration = synthesis.MeasuredDuration,
        };
        return new(updated, synthesis.Fit, attempts, synthesis.Fit.RequiresUserReview);
    }

    private sealed record RewriteResponse(string Text, IReadOnlyList<string> EvidenceIds);
}
