namespace ADFi.LocalAI;

public sealed record CreatorHardwareAssessment(
    HardwareProfile Profile,
    ModelTier SelectedTier);

/// <summary>
/// Profiles inexpensive resources first, selects a tier using currently free RAM/VRAM, and only
/// then runs one bounded inference. The benchmark itself requests immediate model unload.
/// </summary>
public sealed class CreatorHardwareAssessmentCoordinator(
    string workingDirectory,
    IHardwareCommandRunner commandRunner,
    Func<string, IMiniatureInferenceBenchmark> benchmarkFactory)
{
    public async Task<CreatorHardwareAssessment> MeasureAsync(CancellationToken cancellationToken)
    {
        var baselineProfiler = new SystemHardwareProfiler(workingDirectory, commandRunner);
        var baseline = await baselineProfiler.ProfileAsync(cancellationToken).ConfigureAwait(false);
        var selectedTier = ModelTierPolicy.Select(baseline);
        decimal inferenceRate = 0;
        var warnings = baseline.Warnings?
            .Where(item => !item.StartsWith("Local-model throughput", StringComparison.Ordinal))
            .ToList() ?? [];
        try
        {
            inferenceRate = await benchmarkFactory(selectedTier.OllamaModel)
                .MeasureTokensPerSecondAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            warnings.Add($"Local-model benchmark was deferred: {exception.Message}");
        }

        return new(
            baseline with
            {
                MiniatureInferenceTokensPerSecond = inferenceRate,
                Warnings = warnings,
            },
            selectedTier);
    }
}
