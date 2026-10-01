using ADFi.LocalAI;

namespace ADFi.Core.Tests;

public sealed class ModelTierTests
{
    [Fact]
    public void EightGigabytesFreeRamAndVramSelectsBalancedQwen3Vl4B()
    {
        var profile = Profile(8, 8);
        var selected = ModelTierPolicy.Select(profile);
        Assert.Equal(ModelTierPolicy.BalancedQwen3Vl4B, selected.OllamaModel);
    }

    [Fact]
    public void LowVramFallsBackWithoutChangingSchemas()
    {
        var selected = ModelTierPolicy.Select(Profile(4, 0));
        Assert.Equal("Constrained", selected.Name);
    }

    [Fact]
    public void ConsumerEightGigabyteGpuReportingSlightlyUnderEightGibSelectsBalanced()
    {
        var profile = Profile(8, 0) with { DedicatedVideoMemoryBytes = 8_188L * 1024 * 1024 };
        var selected = ModelTierPolicy.Select(profile);
        Assert.Equal("Balanced", selected.Name);
    }

    private static HardwareProfile Profile(int freeRamGib, int vramGib) => new(
        "X64",
        8,
        16L * 1024 * 1024 * 1024,
        freeRamGib * 1024L * 1024 * 1024,
        "Test",
        "Test GPU",
        vramGib * 1024L * 1024 * 1024,
        new HashSet<AccelerationKind>(),
        "Unknown",
        100L * 1024 * 1024 * 1024,
        0,
        0,
        DateTimeOffset.UnixEpoch);
}
