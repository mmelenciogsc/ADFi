using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace ADFi.LocalAI;

public sealed record HardwareCommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface IHardwareCommandRunner
{
    Task<HardwareCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public interface IMiniatureInferenceBenchmark
{
    Task<decimal> MeasureTokensPerSecondAsync(CancellationToken cancellationToken);
}

public sealed class ProcessHardwareCommandRunner : IHardwareCommandRunner
{
    public async Task<HardwareCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start required hardware probe '{executable}'.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }

        return new(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// Profiles the machine without loading a heavyweight model. An optional bounded inference
/// benchmark can be supplied after the selected Ollama package is available. Every job still
/// calls <see cref="GetCurrentFreeResourcesAsync"/> immediately before acquiring a runtime.
/// </summary>
public sealed class SystemHardwareProfiler(
    string workingDirectory,
    IHardwareCommandRunner commandRunner,
    IMiniatureInferenceBenchmark? inferenceBenchmark = null) : IHardwareProfiler
{
    private const long Mebibyte = 1024L * 1024L;

    public async Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var memory = ReadMemory();
        var gpu = await ReadGpuAsync(cancellationToken).ConfigureAwait(false);
        if (gpu is null)
        {
            warnings.Add("No supported discrete-GPU telemetry was available; CPU fallback remains available.");
        }

        var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(workingDirectory))
                                 ?? throw new InvalidOperationException("The working drive could not be resolved."));
        var diskType = await ReadWindowsDiskTypeAsync(disk.Name, cancellationToken).ConfigureAwait(false);
        var transcodeRate = await MeasureTranscodeAsync(cancellationToken).ConfigureAwait(false);
        decimal inferenceRate = 0;
        if (inferenceBenchmark is not null)
        {
            try
            {
                inferenceRate = await inferenceBenchmark.MeasureTokensPerSecondAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                warnings.Add($"Local-model benchmark was deferred: {exception.Message}");
            }
        }
        else
        {
            warnings.Add("Local-model throughput will be measured after the selected Ollama model is installed.");
        }

        return new HardwareProfile(
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount,
            memory.Total,
            memory.Free,
            gpu?.Vendor,
            gpu?.Model,
            gpu?.TotalBytes ?? 0,
            gpu?.FreeBytes ?? 0,
            BuildAccelerationSet(gpu),
            diskType,
            disk.AvailableFreeSpace,
            inferenceRate,
            transcodeRate,
            DateTimeOffset.UtcNow,
            ReadCpuFeatures(),
            warnings);
    }

    public async Task<(long FreeRamBytes, long FreeVramBytes)> GetCurrentFreeResourcesAsync(
        CancellationToken cancellationToken)
    {
        var memory = ReadMemory();
        var gpu = await ReadGpuAsync(cancellationToken).ConfigureAwait(false);
        return (memory.Free, gpu?.FreeBytes ?? 0);
    }

    private async Task<decimal> MeasureTranscodeAsync(CancellationToken cancellationToken)
    {
        const int frameCount = 60;
        var timer = Stopwatch.StartNew();
        HardwareCommandResult result;
        try
        {
            result = await commandRunner.RunAsync(
                "ffmpeg",
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin",
                    "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
                    "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture),
                    "-c:v", "libx264", "-preset", "veryfast", "-f", "null", "-",
                ],
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("FFmpeg is required for the Creator benchmark.", exception);
        }

        timer.Stop();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg Creator benchmark failed: {result.StandardError.Trim()}");
        }

        return timer.Elapsed.TotalSeconds <= 0
            ? 0
            : decimal.Round(frameCount / (decimal)timer.Elapsed.TotalSeconds, 2);
    }

    private async Task<GpuSnapshot?> ReadGpuAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await commandRunner.RunAsync(
                "nvidia-smi",
                ["--query-gpu=name,memory.total,memory.free", "--format=csv,noheader,nounits"],
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                var first = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                var fields = first?.Split(',', StringSplitOptions.TrimEntries);
                if (fields is { Length: >= 3 }
                    && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMiB)
                    && long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var freeMiB))
                {
                    return new("NVIDIA", fields[0], totalMiB * Mebibyte, freeMiB * Mebibyte);
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return null;
    }

    private async Task<string> ReadWindowsDiskTypeAsync(string driveName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Unknown";
        }

        var letter = driveName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':');
        try
        {
            var result = await commandRunner.RunAsync(
                "powershell.exe",
                [
                    "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    $"(Get-Partition -DriveLetter '{letter}' | Get-Disk | Get-PhysicalDisk | Select-Object -First 1 -ExpandProperty MediaType)",
                ],
                cancellationToken).ConfigureAwait(false);
            var value = result.StandardOutput.Trim();
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(value) ? value : "Unknown";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Unknown";
        }
    }

    private static HashSet<AccelerationKind> BuildAccelerationSet(GpuSnapshot? gpu)
    {
        var result = new HashSet<AccelerationKind> { AccelerationKind.Cpu };
        if (string.Equals(gpu?.Vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(AccelerationKind.NvidiaCuda);
        }

        return result;
    }

    private static HashSet<string> ReadCpuFeatures()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Sse2.IsSupported) result.Add("SSE2");
        if (Sse41.IsSupported) result.Add("SSE4.1");
        if (Avx.IsSupported) result.Add("AVX");
        if (Avx2.IsSupported) result.Add("AVX2");
        if (Fma.IsSupported) result.Add("FMA");
        return result;
    }

    private static (long Total, long Free) ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(status))
            {
                throw new InvalidOperationException("Windows did not return system-memory status.");
            }

            return (checked((long)status.TotalPhysical), checked((long)status.AvailablePhysical));
        }

        var info = GC.GetGCMemoryInfo();
        var total = info.TotalAvailableMemoryBytes;
        return (total, Math.Max(0, total - GC.GetTotalMemory(false)));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public MemoryStatusEx() => Length = (uint)Marshal.SizeOf<MemoryStatusEx>();

        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed record GpuSnapshot(string Vendor, string Model, long TotalBytes, long FreeBytes);
}
