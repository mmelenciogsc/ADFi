using ADFi.LocalAI;

namespace ADFi.Core.Tests;

public sealed class CreatorHardwareAssessmentTests
{
    [Fact]
    public async Task ChoosesTierBeforeRunningExactlyOneBoundedBenchmark()
    {
        var runner = new AssessmentRunner();
        var requestedModels = new List<string>();
        var coordinator = new CreatorHardwareAssessmentCoordinator(
            Path.GetTempPath(),
            runner,
            model =>
            {
                requestedModels.Add(model);
                return new AssessmentBenchmark(42.5m);
            });

        var result = await coordinator.MeasureAsync(TestContext.Current.CancellationToken);

        Assert.Single(requestedModels);
        Assert.Equal(result.SelectedTier.OllamaModel, requestedModels[0]);
        Assert.Equal(42.5m, result.Profile.MiniatureInferenceTokensPerSecond);
        Assert.DoesNotContain(
            result.Profile.Warnings ?? [],
            item => item.StartsWith("Local-model throughput", StringComparison.Ordinal));
    }

    private sealed class AssessmentBenchmark(decimal rate) : IMiniatureInferenceBenchmark
    {
        public Task<decimal> MeasureTokensPerSecondAsync(CancellationToken cancellationToken) =>
            Task.FromResult(rate);
    }

    private sealed class AssessmentRunner : IHardwareCommandRunner
    {
        public Task<HardwareCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(executable switch
            {
                "nvidia-smi" => new HardwareCommandResult(0, "RTX Test, 8188, 7900\r\n", string.Empty),
                "ffmpeg" => new HardwareCommandResult(0, string.Empty, string.Empty),
                "powershell.exe" => new HardwareCommandResult(0, "SSD\r\n", string.Empty),
                _ => new HardwareCommandResult(1, string.Empty, "unsupported"),
            });
    }
}
