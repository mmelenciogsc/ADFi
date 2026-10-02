using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class FinalProjectValidationServiceTests
{
    [Fact]
    public async Task VerifiedProjectProducesRenderReadyCheckpointAndAssetAudit()
    {
        using var fixture = await ValidationFixture.CreateAsync();

        var result = await fixture.CreateService().RunAsync(
            fixture.ProjectId,
            fixture.ProjectDirectory,
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.ReadyForRender);
        Assert.Equal("READY_FOR_FINAL_RENDER", result.Manifest.State);
        Assert.Empty(result.Manifest.Issues);
        Assert.Equal(CheckpointStatus.Complete, result.Checkpoint.Status);
        Assert.NotEmpty(Assert.Single(result.Manifest.NarrationAssets).Sha256);
        Assert.True(File.Exists(Path.Combine(fixture.ProjectDirectory, "manifests", "final-project-validation.json")));
    }

    [Fact]
    public async Task MeaningfulSoundCollisionRequiresExplicitStableOverride()
    {
        using var fixture = await ValidationFixture.CreateAsync(SourceAudioKind.MeaningfulNonDialogue);
        var service = fixture.CreateService();

        var first = await service.RunAsync(
            fixture.ProjectId,
            fixture.ProjectDirectory,
            null,
            TestContext.Current.CancellationToken);
        var warning = Assert.Single(first.Manifest.Issues);
        Assert.Equal("MEANINGFUL_SOUND_COLLISION", warning.Code);
        Assert.True(warning.CanOverride);
        Assert.False(first.ReadyForRender);

        var approved = await service.RunAsync(
            fixture.ProjectId,
            fixture.ProjectDirectory,
            new HashSet<string>([warning.IssueId], StringComparer.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.True(approved.ReadyForRender);
        Assert.True(Assert.Single(approved.Manifest.Issues).Overridden);
        Assert.Equal([warning.IssueId], approved.Manifest.AppliedOverrideIssueIds);
    }

    [Fact]
    public async Task DialogueCollisionCannotBeOverridden()
    {
        using var fixture = await ValidationFixture.CreateAsync(SourceAudioKind.Dialogue);
        var service = fixture.CreateService();
        var first = await service.RunAsync(
            fixture.ProjectId,
            fixture.ProjectDirectory,
            null,
            TestContext.Current.CancellationToken);
        var blocker = Assert.Single(first.Manifest.Issues, item => item.Code == "DIALOGUE_COLLISION");
        Assert.False(blocker.CanOverride);
        Assert.False(first.ReadyForRender);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
            fixture.ProjectId,
            fixture.ProjectDirectory,
            new HashSet<string>([blocker.IssueId], StringComparer.Ordinal),
            TestContext.Current.CancellationToken));

        Assert.Equal("BLOCKING_ISSUE_CANNOT_BE_OVERRIDDEN", error.Message);
    }

    [Fact]
    public async Task ModifiedSourceAndSilentNarrationFailClosed()
    {
        using var fixture = await ValidationFixture.CreateAsync(silentNarration: true);
        await File.AppendAllTextAsync(fixture.SourcePath, "changed", TestContext.Current.CancellationToken);

        var result = await fixture.CreateService().RunAsync(
            fixture.ProjectId,
            fixture.ProjectDirectory,
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.ReadyForRender);
        Assert.Contains(result.Manifest.Issues, item => item.Code == "SOURCE_CHANGED" && !item.CanOverride);
        Assert.Contains(result.Manifest.Issues, item => item.Code == "NARRATION_SILENT" && !item.CanOverride);
    }

    [Fact]
    public async Task MissingCitedFrameFailsClosed()
    {
        using var fixture = await ValidationFixture.CreateAsync();
        File.Delete(Path.Combine(fixture.ProjectDirectory, "visual", "frame-1.jpg"));

        var result = await fixture.CreateService().RunAsync(
            fixture.ProjectId,
            fixture.ProjectDirectory,
            null,
            TestContext.Current.CancellationToken);

        var issue = Assert.Single(result.Manifest.Issues, item => item.Code == "EVIDENCE_ASSET_MISSING");
        Assert.Equal("ad-1", issue.SegmentId);
        Assert.False(issue.CanOverride);
        Assert.False(result.ReadyForRender);
    }

    [Fact]
    public async Task MissingMutationAuditBreaksAppendOnlyHistoryValidation()
    {
        using var fixture = await ValidationFixture.CreateAsync();
        _ = await new ProjectMutationService(fixture.ProjectStore, TimeProvider.System).SelectVoiceAsync(
            fixture.ProjectId,
            1,
            "voice-2",
            "Test mutation.",
            TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(fixture.ProjectDirectory, "audit", "mutations", "00000002.json"));

        var result = await fixture.CreateService().RunAsync(
            fixture.ProjectId,
            fixture.ProjectDirectory,
            null,
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Manifest.Issues, item => item.Code == "MUTATION_AUDIT_MISSING" && !item.CanOverride);
        Assert.False(result.ReadyForRender);
    }

    private sealed class ValidationFixture : IDisposable
    {
        private ValidationFixture(
            string root,
            Guid projectId,
            FileProjectVersionStore projectStore,
            FilePipelineCheckpointStore checkpointStore,
            MediaProbeResult probe,
            string sourcePath)
        {
            Root = root;
            ProjectId = projectId;
            ProjectStore = projectStore;
            CheckpointStore = checkpointStore;
            Probe = probe;
            SourcePath = sourcePath;
            ProjectDirectory = Path.Combine(root, projectId.ToString("D"));
        }

        public string Root { get; }
        public Guid ProjectId { get; }
        public string ProjectDirectory { get; }
        public string SourcePath { get; }
        public FileProjectVersionStore ProjectStore { get; }
        public FilePipelineCheckpointStore CheckpointStore { get; }
        public MediaProbeResult Probe { get; }

        public FinalProjectValidationService CreateService() => new(
            ProjectStore,
            CheckpointStore,
            new FixedProbe(Probe),
            TimeProvider.System);

        public static async Task<ValidationFixture> CreateAsync(
            SourceAudioKind? collision = null,
            bool silentNarration = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "ADFi.ValidationTests", Guid.NewGuid().ToString("N"));
            var projectId = Guid.NewGuid();
            var projectDirectory = Path.Combine(root, projectId.ToString("D"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "manifests"));
            var sourcePath = Path.Combine(projectDirectory, "source.mp4");
            await File.WriteAllBytesAsync(sourcePath, Encoding.UTF8.GetBytes("authorized test media"), TestContext.Current.CancellationToken);
            var sourceBytes = new FileInfo(sourcePath);
            var sourceHash = await HashAsync(sourcePath);
            var duration = TimeSpan.FromSeconds(5);
            var probe = new MediaProbeResult("mp4", duration, sourceBytes.Length, false, []);
            var sourceCheckpoint = new PipelineCheckpoint(
                projectId,
                AdficationPhase.SourceIntakeAndAuthorization,
                CheckpointStatus.Complete,
                DateTimeOffset.UnixEpoch,
                "manifests/source-validated.json",
                sourceHash,
                null);
            var source = new SourceIntakeResult(
                new(sourcePath, sourceHash, sourceBytes.Length, sourceBytes.LastWriteTimeUtc, probe, sourceBytes.Length * 3),
                RightsAttestation.Create(Guid.NewGuid(), projectId, DateTimeOffset.UnixEpoch, sourceHash, true),
                sourceCheckpoint);
            var window = new MediaInterval(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            var narrationPath = Path.Combine(projectDirectory, "narration", "ad-1.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(narrationPath)!);
            WriteWave(narrationPath, TimeSpan.FromMilliseconds(500), silentNarration);
            var segment = new AdSegment(
                "ad-1", "scene-1", window, "She places the key down.", ["frame-1"],
                "ADFI_AD_WRITER_SYSTEM", "1.0.0", new string('A', 64), "narration/ad-1.wav",
                TimeSpan.FromMilliseconds(500), true, new string('B', 64));
            var snapshot = new ProjectSnapshot(
                projectId, 1, sourceHash, duration, [segment], "voice-1", DateTimeOffset.UnixEpoch, null);
            var projectStore = new FileProjectVersionStore(root);
            await projectStore.InitializeAsync(snapshot, TestContext.Current.CancellationToken);
            var checkpointStore = new FilePipelineCheckpointStore(root);
            await checkpointStore.SaveAsync(sourceCheckpoint, TestContext.Current.CancellationToken);

            var sourceTimeline = collision is null
                ? new SourceAudioInterval[] { new(window, SourceAudioKind.QuietCandidate, 1m, "quiet-1") }
                : [new(window, collision.Value, 1m, "audio-1")];
            var audio = new AudioAnalysisCompletionManifest(
                projectId,
                sourceHash,
                "tiny",
                new(projectId, sourceHash, "audio/analysis.wav", new(16_000, 1, null, null, []), [], DateTimeOffset.UnixEpoch),
                [],
                sourceTimeline,
                [window],
                DateTimeOffset.UnixEpoch);
            var frame = new KeyframeEvidence("frame-1", TimeSpan.FromSeconds(1), "visual/frame-1.jpg", 640, 360, 0, 0, false);
            var framePath = Path.Combine(projectDirectory, "visual", "frame-1.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(framePath)!);
            await File.WriteAllBytesAsync(framePath, [0xFF, 0xD8, 0xFF, 0xD9], TestContext.Current.CancellationToken);
            var visual = new VisualIndexManifest(
                projectId, sourceHash, TimeSpan.FromSeconds(1), 640, 3,
                [new("scene-1", new(TimeSpan.Zero, duration), [frame], [], ["quiet-1"])],
                DateTimeOffset.UnixEpoch);
            var provenance = new PromptDecisionProvenance(
                "ADFI_VISUAL_ANALYST_SYSTEM", "1.0.0", new string('C', 64), "test", "qwen-test", DateTimeOffset.UnixEpoch);
            var analysis = new VisualUnderstandingManifest(
                projectId,
                sourceHash,
                [new("scene-1", [new("obs-1", "action", "She places the key down.", ["frame-1"], 1m, 1m, false)], [], false, false, null, provenance)],
                DateTimeOffset.UnixEpoch);
            var faces = new FaceContinuityManifest(projectId, sourceHash, [], [], [], DateTimeOffset.UnixEpoch);
            await WriteAsync(projectDirectory, "source-validated.json", source);
            await WriteAsync(projectDirectory, "audio-analysis-complete.json", audio);
            await WriteAsync(projectDirectory, "visual-index.json", visual);
            await WriteAsync(projectDirectory, "visual-analysis-complete.json", analysis);
            await WriteAsync(projectDirectory, "face-continuity.json", faces);
            return new(root, projectId, projectStore, checkpointStore, probe, sourcePath);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static async Task WriteAsync<T>(string projectDirectory, string name, T value)
        {
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "manifests", name),
                JsonSerializer.Serialize(value),
                TestContext.Current.CancellationToken);
        }

        private static async Task<string> HashAsync(string path)
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
        }

        private static void WriteWave(string path, TimeSpan duration, bool silent)
        {
            const int sampleRate = 16_000;
            const short channels = 1;
            const short bits = 16;
            var sampleCount = checked((int)(duration.TotalSeconds * sampleRate));
            var dataSize = sampleCount * sizeof(short);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.ASCII);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bits / 8);
            writer.Write((short)(channels * bits / 8));
            writer.Write(bits);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            for (var index = 0; index < sampleCount; index++)
            {
                var value = silent ? (short)0 : (short)(Math.Sin(2 * Math.PI * 220 * index / sampleRate) * 4_000);
                writer.Write(value);
            }
        }
    }

    private sealed class FixedProbe(MediaProbeResult result) : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(string localVideoPath, CancellationToken cancellationToken) =>
            Task.FromResult(result with { FileSizeBytes = new FileInfo(localVideoPath).Length });
    }
}
