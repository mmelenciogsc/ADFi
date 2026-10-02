using System.Diagnostics;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class CreatorPreparationWorkflowTests
{
    [Fact]
    public async Task ValidatedSourceRunsToSceneIndexAndSecondRunSkipsVerifiedStages()
    {
        var root = Path.Combine(Path.GetTempPath(), "ADFi.Tests", Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "source.mp4");
        var projectId = Guid.NewGuid();
        var projectDirectory = Path.Combine(root, projectId.ToString("D"));
        var workerPath = Path.Combine(root, "worker.py");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(workerPath, "# fake worker used with an in-memory runtime", TestContext.Current.CancellationToken);

        try
        {
            await GenerateFixtureAsync(sourcePath);
            var intake = await new SourceIntakeService(new FfprobeMediaProbe(), TimeProvider.System)
                .ValidateAsync(
                    sourcePath,
                    projectDirectory,
                    Guid.NewGuid(),
                    projectId,
                    true,
                    TestContext.Current.CancellationToken);
            var checkpointStore = new FilePipelineCheckpointStore(root);
            await checkpointStore.SaveAsync(intake.Checkpoint, TestContext.Current.CancellationToken);
            var runtimeCreations = 0;
            var workflow = new CreatorPreparationWorkflow(
                checkpointStore,
                TimeProvider.System,
                () =>
                {
                    runtimeCreations++;
                    return new FixtureWhisperRuntime();
                });
            var configuration = new CreatorPreparationConfiguration(
                "ffmpeg",
                new(Environment.ProcessPath!, workerPath, Path.Combine(root, "models")),
                "tiny",
                320,
                3);

            var first = await workflow.RunAsync(
                projectId,
                projectDirectory,
                configuration,
                null,
                TestContext.Current.CancellationToken);
            var firstCreationCount = runtimeCreations;
            var second = await workflow.RunAsync(
                projectId,
                projectDirectory,
                configuration,
                null,
                TestContext.Current.CancellationToken);

            Assert.NotEmpty(first.AudioAnalysis.Transcript);
            Assert.NotEmpty(first.VisualIndex.Scenes);
            Assert.Equal(first.AudioAnalysis.ProjectId, second.AudioAnalysis.ProjectId);
            Assert.Equal(first.AudioAnalysis.Transcript.Count, second.AudioAnalysis.Transcript.Count);
            Assert.Equal(first.VisualIndex.ProjectId, second.VisualIndex.ProjectId);
            Assert.Equal(first.VisualIndex.Scenes.Count, second.VisualIndex.Scenes.Count);
            Assert.Equal(1, firstCreationCount);
            Assert.Equal(firstCreationCount, runtimeCreations);

            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "manifests", "visual-index.json"),
                "corrupt",
                TestContext.Current.CancellationToken);
            var repaired = await workflow.RunAsync(
                projectId,
                projectDirectory,
                configuration,
                null,
                TestContext.Current.CancellationToken);
            Assert.NotEmpty(repaired.VisualIndex.Scenes);
            Assert.Equal(firstCreationCount, runtimeCreations);
            var checkpoints = await checkpointStore.LoadAsync(projectId, TestContext.Current.CancellationToken);
            Assert.Contains(checkpoints, item =>
                item.Phase == AdficationPhase.AudioCharacterizationAndDialogueSegmentation
                && item.Status == CheckpointStatus.Complete);
            Assert.Contains(checkpoints, item =>
                item.Phase == AdficationPhase.VisualSegmentationAndKeyframes
                && item.Status == CheckpointStatus.Complete);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task GenerateFixtureAsync(string path)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", "testsrc2=s=320x240:r=24:d=1.2",
                     "-f", "lavfi", "-i", "sine=frequency=440:duration=1.2",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg could not create the preparation fixture.");
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class FixtureWhisperRuntime : IWhisperRuntime
    {
        public Task LoadAsync(string modelName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioChunk chunk,
            string absoluteChunkPath,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<TranscriptSegment> result =
            [
                new(
                    chunk.ChunkId + "-segment",
                    chunk.ChunkId,
                    new(chunk.SourceInterval.Start, chunk.SourceInterval.End),
                    "Fixture dialogue.",
                    1m,
                    []),
            ];
            return Task.FromResult(result);
        }

        public Task UnloadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
