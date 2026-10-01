namespace ADFi.LocalAI;

public enum AccelerationKind
{
    Cpu,
    NvidiaCuda,
    AmdSupportedRuntime,
    IntelSupportedRuntime,
    Vulkan,
}

public sealed record HardwareProfile(
    string CpuArchitecture,
    int LogicalProcessors,
    long TotalSystemMemoryBytes,
    long FreeSystemMemoryBytes,
    string? GpuVendor,
    string? GpuModel,
    long DedicatedVideoMemoryBytes,
    IReadOnlySet<AccelerationKind> Acceleration,
    string WorkingDiskType,
    long WorkingDiskFreeBytes,
    decimal MiniatureInferenceTokensPerSecond,
    decimal MiniatureTranscodeFramesPerSecond,
    DateTimeOffset MeasuredAtUtc,
    IReadOnlySet<string>? CpuFeatures = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record ModelTier(
    string Name,
    string OllamaModel,
    long MinimumFreeRamBytes,
    long MinimumVramBytes,
    int MaximumImageDimension,
    int MaximumTemporalFrames);

public static class ModelTierPolicy
{
    public const string BalancedQwen3Vl4B = "qwen3-vl:4b";

    public static IReadOnlyList<ModelTier> Tiers { get; } =
    [
        new("Large", "qwen3-vl:8b", Gibibytes(12), Gibibytes(12), 1344, 5),
        // Consumer "8 GB" GPUs commonly report a few MiB below exactly 8 GiB.
        new("Balanced", BalancedQwen3Vl4B, Gibibytes(8), Mebibytes(7_500), 1024, 3),
        new("Constrained", "qwen3-vl:2b", Gibibytes(4), 0, 768, 3),
    ];

    public static ModelTier Select(HardwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Tiers.FirstOrDefault(tier =>
                   profile.FreeSystemMemoryBytes >= tier.MinimumFreeRamBytes
                   && profile.DedicatedVideoMemoryBytes >= tier.MinimumVramBytes)
               ?? Tiers[^1];
    }

    private static long Gibibytes(int value) => value * 1024L * 1024L * 1024L;

    private static long Mebibytes(int value) => value * 1024L * 1024L;
}

public interface IHardwareProfiler
{
    Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken);

    Task<(long FreeRamBytes, long FreeVramBytes)> GetCurrentFreeResourcesAsync(
        CancellationToken cancellationToken);
}
