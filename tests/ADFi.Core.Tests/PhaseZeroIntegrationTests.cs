using System.Diagnostics;
using ADFi.Core;
using ADFi.Media;
using ADFi.Infrastructure;

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

            var audioService = new AudioPreparationService("ffmpeg", TimeProvider.System);
            var audio = await audioService.PrepareAsync(
                result.Attestation.ProjectId,
                result.Source,
                projectPath,
                cancellationToken);

            Assert.Equal(16_000, audio.Characterization.AnalysisSampleRate);
            Assert.Equal(1, audio.Characterization.AnalysisChannels);
            Assert.NotEmpty(audio.Chunks);
            Assert.All(audio.Chunks, chunk =>
                Assert.True(File.Exists(Path.Combine(
                    projectPath,
                    chunk.RelativeAudioPath.Replace('/', Path.DirectorySeparatorChar)))));
            Assert.True(File.Exists(Path.Combine(projectPath, "manifests", "audio-prepared.json")));
            Assert.Equal(before, await File.ReadAllBytesAsync(sourcePath, cancellationToken));

            var frameCandidates = await OpenCvVisualPreprocessor.AnalyzeCandidatesAsync(
                sourcePath,
                TimeSpan.FromMilliseconds(250),
                maximumDimension: 320,
                cancellationToken);
            Assert.NotEmpty(frameCandidates);
            Assert.Equal(TimeSpan.Zero, frameCandidates[0].SourceTime);
            Assert.True(frameCandidates.Count <= 5);

            var visualIndex = await new VisualIndexService(TimeProvider.System).BuildAsync(
                result.Attestation.ProjectId,
                result.Source,
                projectPath,
                maximumImageDimension: 320,
                maximumFramesPerScene: 3,
                transcript: [],
                narrationWindows: [new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(900))],
                cancellationToken);
            var indexedScene = Assert.Single(visualIndex.Scenes);
            Assert.NotEmpty(indexedScene.Keyframes);
            Assert.All(indexedScene.Keyframes, frame =>
            {
                Assert.True(frame.Width <= 320 && frame.Height <= 320);
                Assert.True(File.Exists(Path.Combine(projectPath, frame.RelativeImagePath.Replace('/', Path.DirectorySeparatorChar))));
            });
            Assert.True(File.Exists(Path.Combine(projectPath, "manifests", "visual-index.json")));

            using (var viewerEvidenceStore = new PrivateEphemeralEvidenceStore(
                       Path.Combine(testRoot, "viewer-evidence")))
            {
                var viewerSource = InteractiveVideoSource.LocalFile(sourcePath);
                var viewerSessionId = Guid.NewGuid();
                var viewerRequestId = Guid.NewGuid();
                var momentEvidence = await new FfmpegLocalMomentEvidenceProvider(
                        viewerEvidenceStore,
                        "ffmpeg",
                        new(3, 600, 320, 5000))
                    .AcquireAsync(
                        new(
                            viewerSessionId,
                            viewerRequestId,
                            viewerSource,
                            500,
                            MomentQuestionClassifier.Classify(
                                "What is happening?",
                                new(3, 600, 320, 5000)),
                            true,
                            viewerEvidenceStore.GetSessionDirectory(viewerSessionId),
                            TimeProvider.System.GetUtcNow()),
                        cancellationToken);
                Assert.Equal(3, momentEvidence.Count);
                Assert.All(momentEvidence, item =>
                {
                    Assert.True(item.Ephemeral);
                    Assert.True(item.PixelWidth <= 320 && item.PixelHeight <= 320);
                    Assert.True(File.Exists(item.PrivateEphemeralPath));
                });
                await viewerEvidenceStore.DeleteRequestAsync(
                    viewerSessionId,
                    viewerRequestId,
                    cancellationToken);
                Assert.All(momentEvidence, item => Assert.False(File.Exists(item.PrivateEphemeralPath)));
            }

            var narrationPath = Path.Combine(testRoot, "narration.wav");
            await GenerateNarrationFixtureAsync(narrationPath);
            var outputPath = Path.Combine(testRoot, "adfied.mp4");
            var renderer = new FinalRenderService("ffmpeg", new FfprobeMediaProbe());
            var render = await renderer.RenderAsync(
                new(
                    sourcePath,
                    result.Source.Probe,
                    [new("ad-1", narrationPath, new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)), TimeSpan.FromMilliseconds(250))],
                    outputPath),
                cancellationToken);

            Assert.True(render.VideoStreamCopied);
            Assert.Equal(64, render.Sha256.Length);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(before, await File.ReadAllBytesAsync(sourcePath, cancellationToken));

            var checkpointStore = new FilePipelineCheckpointStore(projectPath);
            var previewService = new PreviewAssemblyService(
                renderer,
                "ffmpeg",
                checkpointStore,
                TimeProvider.System);
            var previewRequest = new PreviewAssemblyRequest(
                result.Attestation.ProjectId,
                projectPath,
                sourcePath,
                result.Source.Probe,
                [new("ad-1", narrationPath, new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)), TimeSpan.FromMilliseconds(250))],
                new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(900)));
            var preview = await previewService.AssembleAsync(
                previewRequest,
                cancellationToken);
            var restoredPreview = await previewService.AssembleAsync(previewRequest, cancellationToken);

            Assert.Equal("REVIEW_IN_PROGRESS", preview.ReviewState);
            Assert.False(preview.ReusedFullDraft);
            Assert.True(restoredPreview.ReusedFullDraft);
            Assert.Equal(preview.Manifest.UpdatedAtUtc, restoredPreview.Manifest.UpdatedAtUtc);
            Assert.True(File.Exists(preview.FullDraftPath));
            Assert.True(File.Exists(preview.FocusPreviewPath));
            Assert.Equal(AdficationPhase.InitialPreviewAssembly, preview.Checkpoint.Phase);
            Assert.Contains(
                await checkpointStore.LoadAsync(result.Attestation.ProjectId, cancellationToken),
                item => item.Phase == AdficationPhase.InitialPreviewAssembly);
            Assert.Equal(before, await File.ReadAllBytesAsync(sourcePath, cancellationToken));
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

    private static async Task GenerateNarrationFixtureAsync(string path)
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
                     "-f", "lavfi", "-i", "sine=frequency=700:duration=0.25",
                     "-c:a", "pcm_s16le", path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg could not generate the narration fixture.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, error);
    }
}
