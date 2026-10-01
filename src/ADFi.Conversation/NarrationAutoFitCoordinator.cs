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
            var response = await runtime.GenerateAsync(
                modelName,
                new(
                    prompt.Content,
                    JsonSerializer.Serialize(new
                    {
                        operation = "AutoFitNarrationToWindow",
                        currentText,
                        requiredEvidenceIds = segment.EvidenceIds,
                        maximumDurationMilliseconds = checked((long)(segment.Window.Duration - breathingRoom).TotalMilliseconds),
                        renderedDurationMilliseconds = checked((long)renderedDuration.TotalMilliseconds),
                        instruction = "Shorten wording only. Preserve every supported essential fact and every evidence ID. Do not add facts.",
                    }),
                    [],
                    RewriteSchema,
                    350),
                cancellationToken).ConfigureAwait(false);
            var rewrite = JsonSerializer.Deserialize<RewriteResponse>(response.Content, JsonOptions)
                          ?? throw new InvalidDataException("Auto-fit returned no rewrite.");
            ArgumentException.ThrowIfNullOrWhiteSpace(rewrite.Text);
            if (!segment.EvidenceIds.ToHashSet(StringComparer.Ordinal).SetEquals(rewrite.EvidenceIds))
            {
                throw new InvalidDataException("Auto-fit changed the segment's required evidence set.");
            }

            if (rewrite.Text.Length >= currentText.Length)
            {
                throw new InvalidDataException("Auto-fit did not shorten the narration.");
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
