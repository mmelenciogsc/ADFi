using System.Diagnostics;
using ADFi.Core;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class PhaseZeroIntegrationTests
{
    [Fact]
    public async Task LocalVideoBecomesAnImmutableValidatedSourceManifest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testRoot = Path.Combine(Path.GetTempPath(), "ADFi.Tests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(testRoot, "source.mp4");
        var projectPath = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(testRoot);

        try
        {
            await GenerateFixtureAsync(sourcePath);
            var before = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            var service = new SourceIntakeService(new FfprobeMediaProbe(), TimeProvider.System);

            var result = await service.ValidateAsync(
                sourcePath,
                projectPath,
                Guid.NewGuid(),
                Guid.NewGuid(),
                rightsAccepted: true,
                cancellationToken);

            var after = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            Assert.Equal(before, after);
            Assert.Equal(CheckpointStatus.Complete, result.Checkpoint.Status);
            Assert.Equal(AdficationPhase.SourceIntakeAndAuthorization, result.Checkpoint.Phase);
            Assert.Equal(64, result.Source.SourceSha256.Length);
            Assert.Contains(result.Source.Probe.Streams, stream => stream.CodecType == "video");
            Assert.Contains(result.Source.Probe.Streams, stream => stream.CodecType == "audio");
            Assert.True(File.Exists(Path.Combine(projectPath, "manifests", "source-validated.json")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task GenerateFixtureAsync(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", "color=c=blue:s=320x240:d=1",
                     "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg could not be started for the integration fixture.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, error);
    }
}
