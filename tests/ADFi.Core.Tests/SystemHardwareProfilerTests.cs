using ADFi.LocalAI;

namespace ADFi.Core.Tests;

public sealed class SystemHardwareProfilerTests
{
    [Fact]
    public async Task ProfileRecordsGpuDiskCpuAndBoundedBenchmark()
    {
        var runner = new FakeRunner();
        var profiler = new SystemHardwareProfiler(
            Path.GetTempPath(),
            runner,
            new FixedInferenceBenchmark());

        var profile = await profiler.ProfileAsync(TestContext.Current.CancellationToken);

        Assert.True(profile.TotalSystemMemoryBytes > 0);
        Assert.Equal("NVIDIA", profile.GpuVendor);
        Assert.Equal("Test GPU", profile.GpuModel);
        Assert.Equal(8192L * 1024 * 1024, profile.DedicatedVideoMemoryBytes);
        Assert.Equal(6144L * 1024 * 1024, profile.FreeVideoMemoryBytes);
        Assert.Contains(AccelerationKind.NvidiaCuda, profile.Acceleration);
        Assert.Equal(17.5m, profile.MiniatureInferenceTokensPerSecond);
        Assert.True(profile.MiniatureTranscodeFramesPerSecond > 0);
        Assert.Contains(runner.Calls, call => call.Executable == "ffmpeg");
    }

    [Fact]
    public async Task DynamicSnapshotRefreshesFreeGpuMemoryPerJob()
    {
        var runner = new FakeRunner();
        var profiler = new SystemHardwareProfiler(Path.GetTempPath(), runner);

        var first = await profiler.GetCurrentFreeResourcesAsync(TestContext.Current.CancellationToken);
        var second = await profiler.GetCurrentFreeResourcesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6144L * 1024 * 1024, first.FreeVramBytes);
        Assert.Equal(first.FreeVramBytes, second.FreeVramBytes);
        Assert.True(first.FreeRamBytes > 0);
        Assert.Equal(2, runner.Calls.Count(call => call.Executable == "nvidia-smi"));
    }

    private sealed class FixedInferenceBenchmark : IMiniatureInferenceBenchmark
    {
        public Task<decimal> MeasureTokensPerSecondAsync(CancellationToken cancellationToken) =>
            Task.FromResult(17.5m);
    }

    private sealed class FakeRunner : IHardwareCommandRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<HardwareCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Calls.Add((executable, arguments));
            return Task.FromResult(executable switch
            {
                "nvidia-smi" => new HardwareCommandResult(0, "Test GPU, 8192, 6144\r\n", string.Empty),
                "ffmpeg" => new HardwareCommandResult(0, string.Empty, string.Empty),
                "powershell.exe" => new HardwareCommandResult(0, "SSD\r\n", string.Empty),
                _ => new HardwareCommandResult(1, string.Empty, "unsupported"),
            });
        }
    }
}
