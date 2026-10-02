using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class CreatorPreviewWorkflowTests
{
    [Fact]
    public async Task NarratedProjectBuildsRestoresRefocusesAndRepairsVerifiedPreview()
    {
        var root = Path.Combine(Path.GetTempPath(), "ADFi.Tests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "source.mp4");
        var projectId = Guid.NewGuid();
        var project = Path.Combine(root, projectId.ToString("D"));
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root);
        try
        {
            await GenerateVideoAsync(sourcePath, cancellationToken);
            var intake = await new SourceIntakeService(new FfprobeMediaProbe(), TimeProvider.System)
                .ValidateAsync(sourcePath, project, Guid.NewGuid(), projectId, true, cancellationToken);
            var sourceBefore = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            var frame = new KeyframeEvidence(
                "frame-1", TimeSpan.FromMilliseconds(300), "visual/frames/frame.jpg", 320, 240, 0, 0, false);
            var visual = new VisualIndexManifest(
                projectId,
                intake.Source.SourceSha256,
                TimeSpan.FromMilliseconds(500),
                320,
                3,
                [new("scene-1", new(TimeSpan.Zero, intake.Source.Probe.Duration), [frame], [], [])],
                DateTimeOffset.UnixEpoch);
            var narrationRelative = "narration/cache/segment.wav";
            var narrationPath = Path.Combine(project, "narration", "cache", "segment.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(narrationPath)!);
            WriteWave(narrationPath, TimeSpan.FromMilliseconds(250));
            var window = new MediaInterval(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800));
            var segment = new AdSegment(
                "ad-1",
                "scene-1",
                window,
                "A white title appears.",
                ["frame-1"],
                "ADFI_AD_WRITER_SYSTEM",
                "1.0.0",
                new string('A', 64),
                narrationRelative,
                TimeSpan.FromMilliseconds(250),
                false,
                new string('B', 64));
            var fit = NarrationTimingPolicy.Evaluate(
                TimeSpan.FromMilliseconds(250), window.Duration, TimeSpan.FromMilliseconds(100), 1);
            var draftPath = Path.Combine(project, "manifests", "ad-draft-complete.json");
            await WriteAsync(
                draftPath,
                new AdDraftCompletionManifest(projectId, [segment], intake.Source.SourceSha256),
                cancellationToken);
            var narration = new NarrationCompletionManifest(
                projectId,
                intake.Source.SourceSha256,
                await HashAsync(draftPath, cancellationToken),
                "voice-1",
                [
                    new(
                        "voice-1",
                        segment,
                        fit,
                        1,
                        false,
                        await HashAsync(narrationPath, cancellationToken)),
                ],
                DateTimeOffset.UnixEpoch);
            await WriteAsync(Path.Combine(project, "manifests", "visual-index.json"), visual, cancellationToken);
            await WriteAsync(Path.Combine(project, "manifests", "narration-complete.json"), narration, cancellationToken);

            var checkpointStore = new FilePipelineCheckpointStore(root);
            await checkpointStore.SaveAsync(intake.Checkpoint, cancellationToken);
            var workflow = new CreatorPreviewWorkflow(new PreviewAssemblyService(
                new FinalRenderService("ffmpeg", new FfprobeMediaProbe()),
                "ffmpeg",
                checkpointStore,
                TimeProvider.System));
            var first = await workflow.RunAsync(projectId, project, null, null, cancellationToken);
            var second = await workflow.RunAsync(projectId, project, null, null, cancellationToken);
            var focusWindow = new MediaInterval(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(900));
            var focused = await workflow.RunAsync(projectId, project, focusWindow, null, cancellationToken);

            Assert.False(first.ReusedFullDraft);
            Assert.True(second.ReusedFullDraft);
            Assert.True(focused.ReusedFullDraft);
            Assert.True(File.Exists(focused.FocusPreviewPath));
            Assert.Equal(first.Manifest.FullDraftSha256, focused.Manifest.FullDraftSha256);
            Assert.Equal(focusWindow, focused.Manifest.FocusWindow);

            await File.WriteAllBytesAsync(first.FullDraftPath, [1, 2, 3], cancellationToken);
            var repaired = await workflow.RunAsync(projectId, project, null, null, cancellationToken);

            Assert.False(repaired.ReusedFullDraft);
            Assert.Equal(
                repaired.Manifest.FullDraftSha256,
                await HashAsync(repaired.FullDraftPath, cancellationToken));
            Assert.Null(repaired.FocusPreviewPath);
            Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(sourcePath, cancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task GenerateVideoAsync(string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", "color=c=purple:s=320x240:d=1",
                     "-f", "lavfi", "-i", "sine=frequency=330:duration=1",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("FFmpeg could not create the preview fixture.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, error);
    }

    private static void WriteWave(string path, TimeSpan duration)
    {
        const int sampleRate = 16_000;
        const short channels = 1;
        const short bits = 16;
        var bytesPerSecond = sampleRate * channels * bits / 8;
        var dataSize = checked((int)(duration.TotalSeconds * bytesPerSecond));
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(bytesPerSecond);
        writer.Write((short)(channels * bits / 8));
        writer.Write(bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value), cancellationToken);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
