using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class CreatorFinalizationWorkflowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public async Task CurrentValidatedVersionProducesCompleteExportBundle()
    {
        using var fixture = await FinalizationFixture.CreateAsync();

        var result = await fixture.CreateWorkflow().RunAsync(
            new(fixture.ProjectId, fixture.ProjectDirectory),
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.ReusedVerifiedBundle);
        Assert.Equal("FINAL_RENDER_COMPLETE", result.Manifest.State);
        Assert.True(result.Manifest.VideoStreamCopied);
        Assert.True(File.Exists(result.FinalVideoPath));
        Assert.True(File.Exists(result.AdScriptPath));
        Assert.True(File.Exists(result.ProjectExportPath));
        Assert.Single(result.Manifest.NarrationAssets);
        Assert.Equal(AdficationPhase.FinalMp4Rendering, result.Checkpoint.Phase);
        Assert.Equal(CheckpointStatus.Complete, result.Checkpoint.Status);
        Assert.Contains(
            "She places the key down.",
            await File.ReadAllTextAsync(result.AdScriptPath, TestContext.Current.CancellationToken));
        Assert.Equal(
            "authorized source",
            await File.ReadAllTextAsync(fixture.SourcePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifiedBundleIsReusedWithoutRenderingAgain()
    {
        using var fixture = await FinalizationFixture.CreateAsync();
        var workflow = fixture.CreateWorkflow();
        var request = new CreatorFinalizationRequest(fixture.ProjectId, fixture.ProjectDirectory);

        _ = await workflow.RunAsync(request, null, TestContext.Current.CancellationToken);
        var restored = await workflow.RunAsync(request, null, TestContext.Current.CancellationToken);

        Assert.True(restored.ReusedVerifiedBundle);
        Assert.Equal(1, fixture.Renderer.CallCount);
    }

    [Fact]
    public async Task ChangedRenderProfileProducesANewBundleInsteadOfReusingBrokenMedia()
    {
        using var fixture = await FinalizationFixture.CreateAsync();
        var request = new CreatorFinalizationRequest(fixture.ProjectId, fixture.ProjectDirectory);
        var first = await fixture.CreateWorkflow().RunAsync(
            request,
            null,
            TestContext.Current.CancellationToken);

        fixture.Renderer.ProfileVersion = "adfi-final-render-v2-test";
        var rebuilt = await fixture.CreateWorkflow().RunAsync(
            request,
            null,
            TestContext.Current.CancellationToken);

        Assert.False(rebuilt.ReusedVerifiedBundle);
        Assert.Equal(2, fixture.Renderer.CallCount);
        Assert.NotEqual(first.FinalVideoPath, rebuilt.FinalVideoPath);
        Assert.Equal(fixture.Renderer.ProfileVersion, rebuilt.Manifest.RenderProfileVersion);
    }

    [Fact]
    public async Task EquivalentValidationRerunDoesNotDuplicateFinalMedia()
    {
        using var fixture = await FinalizationFixture.CreateAsync();
        var workflow = fixture.CreateWorkflow();
        var request = new CreatorFinalizationRequest(fixture.ProjectId, fixture.ProjectDirectory);
        _ = await workflow.RunAsync(request, null, TestContext.Current.CancellationToken);
        var validationPath = Path.Combine(
            fixture.ProjectDirectory,
            "manifests",
            "final-project-validation.json");
        FinalProjectValidationManifest validation;
        await using (var input = File.OpenRead(validationPath))
        {
            validation = (await JsonSerializer.DeserializeAsync<FinalProjectValidationManifest>(
                input,
                JsonOptions,
                cancellationToken: TestContext.Current.CancellationToken))!;
        }

        await using (var output = File.Create(validationPath))
        {
            await JsonSerializer.SerializeAsync(
                output,
                validation with { CompletedAtUtc = validation.CompletedAtUtc.AddMinutes(1) },
                JsonOptions,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await fixture.CheckpointStore.SaveAsync(
            new(
                fixture.ProjectId,
                AdficationPhase.FinalProjectValidation,
                CheckpointStatus.Complete,
                DateTimeOffset.UtcNow,
                "manifests/final-project-validation.json",
                await HashAsync(validationPath),
                null),
            TestContext.Current.CancellationToken);

        var restored = await workflow.RunAsync(request, null, TestContext.Current.CancellationToken);

        Assert.True(restored.ReusedVerifiedBundle);
        Assert.Equal(1, fixture.Renderer.CallCount);
    }

    [Fact]
    public async Task CorruptCompletedVideoIsNeverSilentlyReusedOrOverwritten()
    {
        using var fixture = await FinalizationFixture.CreateAsync();
        var workflow = fixture.CreateWorkflow();
        var request = new CreatorFinalizationRequest(fixture.ProjectId, fixture.ProjectDirectory);
        var first = await workflow.RunAsync(request, null, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            first.FinalVideoPath,
            "corrupt",
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => workflow.RunAsync(
            request,
            null,
            TestContext.Current.CancellationToken));

        Assert.StartsWith("FINAL_BUNDLE_CONFLICT", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Renderer.CallCount);
    }

    [Fact]
    public async Task FailedRenderRemovesOnlyItsPrivatePartialBundle()
    {
        using var fixture = await FinalizationFixture.CreateAsync();
        fixture.Renderer.ThrowAfterWriting = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateWorkflow().RunAsync(
            new(fixture.ProjectId, fixture.ProjectDirectory),
            null,
            TestContext.Current.CancellationToken));

        var finalRoot = Path.Combine(fixture.ProjectDirectory, "final");
        Assert.Empty(Directory.Exists(finalRoot)
            ? Directory.EnumerateDirectories(finalRoot, ".phase12-*.partial")
            : []);
        var checkpoint = Assert.Single(
            await fixture.CheckpointStore.LoadAsync(fixture.ProjectId, TestContext.Current.CancellationToken),
            item => item.Phase == AdficationPhase.FinalMp4Rendering);
        Assert.Equal(CheckpointStatus.Failed, checkpoint.Status);
    }

    [Fact]
    public async Task StaleProjectVersionCannotUseEarlierValidation()
    {
        using var fixture = await FinalizationFixture.CreateAsync();
        var current = await fixture.ProjectStore.LoadCurrentAsync(
            fixture.ProjectId,
            TestContext.Current.CancellationToken);
        await fixture.ProjectStore.AppendAsync(
            current with { Version = 2, UpdatedAtUtc = DateTimeOffset.UtcNow },
            new(
                Guid.NewGuid(),
                fixture.ProjectId,
                1,
                2,
                ProjectMutationKind.SelectNarrationVoice,
                "Test state change.",
                DateTimeOffset.UtcNow),
            1,
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateWorkflow().RunAsync(
            new(fixture.ProjectId, fixture.ProjectDirectory),
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal("FINAL_VALIDATION_NOT_CURRENT", error.Message);
        Assert.Equal(0, fixture.Renderer.CallCount);
    }

    [Fact]
    public async Task ChangedSourceAfterValidationFailsClosed()
    {
        using var fixture = await FinalizationFixture.CreateAsync();
        await File.AppendAllTextAsync(
            fixture.SourcePath,
            "changed",
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateWorkflow().RunAsync(
            new(fixture.ProjectId, fixture.ProjectDirectory),
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal("SOURCE_CHANGED_AFTER_FINAL_VALIDATION", error.Message);
        Assert.Equal(0, fixture.Renderer.CallCount);
    }

    [Fact]
    public async Task NarrationExportCanBeExcludedWithoutChangingFinalMedia()
    {
        using var fixture = await FinalizationFixture.CreateAsync();

        var result = await fixture.CreateWorkflow().RunAsync(
            new(fixture.ProjectId, fixture.ProjectDirectory, IncludeNarrationAssets: false),
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Manifest.NarrationAssetsIncluded);
        Assert.Empty(result.Manifest.NarrationAssets);
        Assert.True(File.Exists(result.FinalVideoPath));
    }

    private sealed class FinalizationFixture : IDisposable
    {
        private FinalizationFixture(
            string root,
            Guid projectId,
            string sourcePath,
            FileProjectVersionStore projectStore,
            FilePipelineCheckpointStore checkpointStore,
            MediaProbeResult probe,
            FakeRenderer renderer)
        {
            Root = root;
            ProjectId = projectId;
            SourcePath = sourcePath;
            ProjectStore = projectStore;
            CheckpointStore = checkpointStore;
            Probe = probe;
            Renderer = renderer;
        }

        public string Root { get; }
        public Guid ProjectId { get; }
        public string ProjectDirectory => Path.Combine(Root, ProjectId.ToString("D"));
        public string SourcePath { get; }
        public FileProjectVersionStore ProjectStore { get; }
        public FilePipelineCheckpointStore CheckpointStore { get; }
        public MediaProbeResult Probe { get; }
        public FakeRenderer Renderer { get; }

        public CreatorFinalizationWorkflow CreateWorkflow() => new(
            ProjectStore,
            CheckpointStore,
            new FixedProbe(Probe),
            Renderer,
            TimeProvider.System);

        public static async Task<FinalizationFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ADFi.FinalizationTests", Guid.NewGuid().ToString("N"));
            var projectId = Guid.NewGuid();
            var projectDirectory = Path.Combine(root, projectId.ToString("D"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "manifests"));
            var sourcePath = Path.Combine(projectDirectory, "source.mp4");
            await File.WriteAllTextAsync(sourcePath, "authorized source", TestContext.Current.CancellationToken);
            var sourceHash = await HashAsync(sourcePath);
            var sourceInfo = new FileInfo(sourcePath);
            var duration = TimeSpan.FromSeconds(5);
            var probe = new MediaProbeResult(
                "mp4",
                duration,
                sourceInfo.Length,
                false,
                [new("video", "h264", 1280, 720, 24, null, null, null)]);
            var sourceCheckpoint = new PipelineCheckpoint(
                projectId,
                AdficationPhase.SourceIntakeAndAuthorization,
                CheckpointStatus.Complete,
                DateTimeOffset.UnixEpoch,
                "manifests/source-validated.json",
                sourceHash,
                null);
            var source = new SourceIntakeResult(
                new(sourcePath, sourceHash, sourceInfo.Length, sourceInfo.LastWriteTimeUtc, probe, sourceInfo.Length * 3),
                RightsAttestation.Create(Guid.NewGuid(), projectId, DateTimeOffset.UnixEpoch, sourceHash, true),
                sourceCheckpoint);
            await WriteJsonAsync(Path.Combine(projectDirectory, "manifests", "source-validated.json"), source);

            var narrationPath = Path.Combine(projectDirectory, "narration", "ad-1.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(narrationPath)!);
            await File.WriteAllTextAsync(narrationPath, "narration", TestContext.Current.CancellationToken);
            var narrationHash = await HashAsync(narrationPath);
            var measured = TimeSpan.FromMilliseconds(500);
            var segment = new AdSegment(
                "ad-1",
                "scene-1",
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
                "She places the key down.",
                ["frame-1"],
                "ADFI_AD_WRITER_SYSTEM",
                "1.0.0",
                new string('A', 64),
                "narration/ad-1.wav",
                measured,
                true,
                new string('B', 64));
            var snapshot = new ProjectSnapshot(
                projectId,
                1,
                sourceHash,
                duration,
                [segment],
                "voice-1",
                DateTimeOffset.UnixEpoch,
                null);
            var projectStore = new FileProjectVersionStore(root);
            await projectStore.InitializeAsync(snapshot, TestContext.Current.CancellationToken);
            var checkpointStore = new FilePipelineCheckpointStore(root);
            await checkpointStore.SaveAsync(sourceCheckpoint, TestContext.Current.CancellationToken);

            var validation = new FinalProjectValidationManifest(
                projectId,
                snapshot.Version,
                sourceHash,
                FinalProjectValidationBinding.ComputeProjectStateSha256(snapshot),
                duration,
                "READY_FOR_FINAL_RENDER",
                [],
                [new("ad-1", "narration/ad-1.wav", narrationHash, measured, 0.5m, 0.2m, 0, 0)],
                [],
                DateTimeOffset.UnixEpoch);
            var validationPath = Path.Combine(projectDirectory, "manifests", "final-project-validation.json");
            await WriteJsonAsync(validationPath, validation);
            await checkpointStore.SaveAsync(
                new(
                    projectId,
                    AdficationPhase.FinalProjectValidation,
                    CheckpointStatus.Complete,
                    DateTimeOffset.UnixEpoch,
                    "manifests/final-project-validation.json",
                    await HashAsync(validationPath),
                    null),
                TestContext.Current.CancellationToken);
            return new(root, projectId, sourcePath, projectStore, checkpointStore, probe, new FakeRenderer(duration));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static async Task WriteJsonAsync<T>(string path, T value)
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakeRenderer(TimeSpan duration) : IFinalMediaRenderer
    {
        public int CallCount { get; private set; }
        public bool ThrowAfterWriting { get; set; }
        public string ProfileVersion { get; set; } = "adfi-final-render-v1-test";
        public string RenderProfileVersion => ProfileVersion;

        public async Task<FinalRenderResult> RenderAsync(
            FinalRenderRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await File.WriteAllTextAsync(request.OutputPath, "final media", cancellationToken);
            if (ThrowAfterWriting)
            {
                throw new InvalidOperationException("Synthetic render failure.");
            }

            return new(request.OutputPath, await HashAsync(request.OutputPath), true, duration);
        }
    }

    private sealed class FixedProbe(MediaProbeResult result) : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
    }
}
