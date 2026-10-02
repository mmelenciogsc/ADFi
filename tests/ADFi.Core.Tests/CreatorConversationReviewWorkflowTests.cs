using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class CreatorConversationReviewWorkflowTests
{
    [Fact]
    public async Task ValidatedRewriteRunsOneTaskAtATimeCommitsAndPersistsAudit()
    {
        var fixture = await ProjectFixture.CreateAsync();
        try
        {
            var plan = new TaskPlan(
                TaskPlanValidator.CurrentSchemaVersion,
                "Shorten this, then play it",
                [
                    PlanTask("task-1", "RewriteAdSegment"),
                    PlanTask("task-2", "CheckForSpoilers", "task-1"),
                    PlanTask("task-3", "CheckRedundancy", "task-2"),
                    PlanTask("task-4", "ValidateNarrationWindow", "task-3"),
                    PlanTask("task-5", "SynthesizeNarration", "task-4"),
                    PlanTask("task-6", "ApplyProjectMutation", "task-5"),
                    PlanTask("task-7", "GenerateVideoPreview", "task-6"),
                ]);
            var responses = new ConcurrentQueue<string>(
            [
                JsonSerializer.Serialize(plan),
                Decision("task-1", "A shorter proposal is ready.", "A woman places a key on the table.", ["frame-1"]),
                Decision("task-2", "No premature identity is introduced."),
                Decision("task-3", "The wording does not repeat dialogue."),
                Decision("task-4", "The wording should fit."),
                Decision("task-5", "Host synthesis may proceed."),
                Decision("task-6", "The host may commit the validated result."),
                Decision("task-7", "The host may build a focused preview."),
            ]);
            var runtime = new RuntimeState(responses);
            var workflow = CreateWorkflow(fixture, runtime);

            var result = await workflow.RunAsync(
                fixture.ProjectId,
                fixture.ProjectDirectory,
                plan.UserRequest,
                "ad-1",
                Configuration(),
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, result.Snapshot.Version);
            Assert.Equal("A woman places a key on the table.", Assert.Single(result.Snapshot.AdSegments).Text);
            Assert.True(result.Snapshot.AdSegments[0].UserApproved);
            Assert.NotNull(result.Snapshot.AdSegments[0].NarrationAssetRelativePath);
            Assert.True(result.Manifest.PreviewRequested);
            Assert.NotNull(result.Manifest.PreviewWindow);
            Assert.All(result.Manifest.Results, item => Assert.True(item.Succeeded));
            Assert.All(result.Manifest.Results, item => Assert.NotNull(item.Provenance));
            Assert.Equal(8, runtime.GenerationCount);
            Assert.Equal(8, runtime.UnloadCount);
            Assert.Equal(1, runtime.MaximumConcurrentGenerations);
            var turnRoot = Path.Combine(
                fixture.ProjectDirectory,
                "conversation",
                "turns",
                result.Manifest.TurnId.ToString("D"));
            Assert.True(File.Exists(Path.Combine(turnRoot, "turn.json")));
            Assert.Equal(7, Directory.GetFiles(Path.Combine(turnRoot, "tasks"), "*.json").Length);
            Assert.Equal(2, (await fixture.Store.LoadCurrentAsync(
                fixture.ProjectId,
                TestContext.Current.CancellationToken)).Version);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task MutationWithoutValidatedNarrationFailsClosedAndPreservesVersion()
    {
        var fixture = await ProjectFixture.CreateAsync();
        try
        {
            var plan = new TaskPlan(
                TaskPlanValidator.CurrentSchemaVersion,
                "Fix this description",
                [PlanTask("task-1", "ApplyProjectMutation")]);
            var runtime = new RuntimeState(new ConcurrentQueue<string>(
            [
                JsonSerializer.Serialize(plan),
                Decision("task-1", "The host should verify whether a mutation is ready."),
            ]));

            var result = await CreateWorkflow(fixture, runtime).RunAsync(
                fixture.ProjectId,
                fixture.ProjectDirectory,
                plan.UserRequest,
                "ad-1",
                Configuration(),
                null,
                TestContext.Current.CancellationToken);

            var failed = Assert.Single(result.Manifest.Results);
            Assert.False(failed.Succeeded);
            Assert.Equal("MUTATION_HAS_NO_VALIDATED_NARRATION", failed.ErrorCode);
            Assert.Equal(1, result.Snapshot.Version);
            Assert.Equal(1, (await fixture.Store.LoadCurrentAsync(
                fixture.ProjectId,
                TestContext.Current.CancellationToken)).Version);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task TrivialSceneQuestionBypassesPlannerAndUsesVerifiedEvidence()
    {
        var fixture = await ProjectFixture.CreateAsync();
        try
        {
            var runtime = new RuntimeState(new ConcurrentQueue<string>(
            [
                Decision(
                    "task-001",
                    "A woman places a brass key on a wooden table.",
                    evidenceIds: ["frame-1"]),
            ]));

            var result = await CreateWorkflow(fixture, runtime).RunAsync(
                fixture.ProjectId,
                fixture.ProjectDirectory,
                "What happens here?",
                "ad-1",
                Configuration(),
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal(ConversationIntent.TrivialVqa, result.Manifest.Intent);
            Assert.Null(result.Manifest.PlannerProvenance);
            Assert.Equal(1, runtime.GenerationCount);
            Assert.Contains("brass key", result.Manifest.Response, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, result.Snapshot.Version);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task VoiceChangeRegeneratesEverySegmentBeforeOneAtomicCommit()
    {
        var fixture = await ProjectFixture.CreateAsync();
        try
        {
            var plan = new TaskPlan(
                TaskPlanValidator.CurrentSchemaVersion,
                "Use voice 3",
                [
                    PlanTask("task-1", "SynthesizeNarration"),
                    PlanTask("task-2", "ApplyProjectMutation", "task-1"),
                ]);
            var runtime = new RuntimeState(new ConcurrentQueue<string>(
            [
                JsonSerializer.Serialize(plan),
                Decision("task-1", "Host synthesis may use voice 3", requestedVoiceId: "voice-3"),
                Decision("task-2", "The measured voice replacement may be committed."),
            ]));

            var result = await CreateWorkflow(fixture, runtime).RunAsync(
                fixture.ProjectId,
                fixture.ProjectDirectory,
                plan.UserRequest,
                "ad-1",
                Configuration(),
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, result.Snapshot.Version);
            Assert.Equal("voice-3", result.Snapshot.NarrationVoiceId);
            Assert.Contains("voice-3", Assert.Single(result.Snapshot.AdSegments).NarrationAssetRelativePath, StringComparison.Ordinal);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task UndoCreatesACompensatingVersionThroughConversation()
    {
        var fixture = await ProjectFixture.CreateAsync();
        try
        {
            _ = await new ProjectMutationService(fixture.Store, TimeProvider.System).SelectVoiceAsync(
                fixture.ProjectId,
                1,
                "voice-2",
                "Test edit.",
                TestContext.Current.CancellationToken);
            var plan = new TaskPlan(
                TaskPlanValidator.CurrentSchemaVersion,
                "Undo that",
                [PlanTask("task-1", "UndoLastMutation")]);
            var runtime = new RuntimeState(new ConcurrentQueue<string>(
            [
                JsonSerializer.Serialize(plan),
                Decision("task-1", "The host may append a compensating version."),
            ]));

            var result = await CreateWorkflow(fixture, runtime).RunAsync(
                fixture.ProjectId,
                fixture.ProjectDirectory,
                plan.UserRequest,
                "ad-1",
                Configuration(),
                null,
                TestContext.Current.CancellationToken);

            Assert.Equal(3, result.Snapshot.Version);
            Assert.Equal(1, result.Snapshot.RestoredFromVersion);
            Assert.Equal("voice-1", result.Snapshot.NarrationVoiceId);
            Assert.Equal([1, 2, 3], (await fixture.Store.LoadHistoryAsync(
                fixture.ProjectId,
                TestContext.Current.CancellationToken)).Select(item => item.Version));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static CreatorConversationReviewWorkflow CreateWorkflow(
        ProjectFixture fixture,
        RuntimeState runtime) => new(
            fixture.Store,
            () => new QueueRuntime(runtime),
            root => new FixedNarrationSynthesizer(root),
            TimeProvider.System);

    private static CreatorConversationConfiguration Configuration()
    {
        var root = FindRepositoryRoot();
        return new(
            "qwen-test",
            Path.Combine(root, "prompts", "ADFI_PLANNER_SYSTEM.md"),
            Path.Combine(root, "prompts", "ADFI_TASK_EXECUTOR_SYSTEM.md"),
            Path.Combine(root, "prompts", "semantic-tools.json"),
            Path.Combine(root, "prompts", "ADFI_AD_WRITER_SYSTEM.md"),
            "voice-1",
            TimeSpan.FromMilliseconds(250));
    }

    private static AtomicTask PlanTask(string id, string tool, string? dependency = null) =>
        new(
            id,
            $"Execute {tool}",
            $"Validated {tool} result",
            dependency is null ? [] : [dependency],
            [tool]);

    private static string Decision(
        string taskId,
        string summary,
        string? proposedText = null,
        IReadOnlyList<string>? evidenceIds = null,
        string? requestedVoiceId = null) => JsonSerializer.Serialize(new AtomicTaskDecision(
        taskId,
        true,
        summary,
        proposedText,
        requestedVoiceId,
        evidenceIds ?? [],
        null));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ADFi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class RuntimeState(ConcurrentQueue<string> responses)
    {
        private int generationCount;
        private int unloadCount;
        private int activeGenerations;
        private int maximumConcurrentGenerations;

        public ConcurrentQueue<string> Responses { get; } = responses;
        public int GenerationCount => generationCount;
        public int UnloadCount => unloadCount;
        public int MaximumConcurrentGenerations => maximumConcurrentGenerations;

        public void BeginGeneration()
        {
            Interlocked.Increment(ref generationCount);
            var active = Interlocked.Increment(ref activeGenerations);
            int observed;
            do
            {
                observed = maximumConcurrentGenerations;
                if (observed >= active) break;
            } while (Interlocked.CompareExchange(ref maximumConcurrentGenerations, active, observed) != observed);
        }

        public void EndGeneration() => Interlocked.Decrement(ref activeGenerations);

        public void Unloaded() => Interlocked.Increment(ref unloadCount);
    }

    private sealed class QueueRuntime(RuntimeState state) : ILocalModelRuntime
    {
        public string RuntimeName => "test-runtime";

        public Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task<LocalModelResponse> GenerateAsync(
            string modelName,
            LocalModelRequest request,
            CancellationToken cancellationToken)
        {
            state.BeginGeneration();
            try
            {
                await Task.Yield();
                return state.Responses.TryDequeue(out var response)
                    ? new(response, modelName, TimeSpan.Zero)
                    : throw new InvalidOperationException("No queued model response remains.");
            }
            finally
            {
                state.EndGeneration();
            }
        }

        public Task UnloadAsync(string modelName, CancellationToken cancellationToken)
        {
            state.Unloaded();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedNarrationSynthesizer(string projectRoot) : INarrationSynthesizer
    {
        public Task<NarrationSynthesisResult> SynthesizeAsync(
            NarrationSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            var voice = PiperVoiceCatalog.Get(request.VoiceId);
            var path = Path.Combine(projectRoot, "narration", "cache", $"{request.SegmentId}-{request.VoiceId}.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteWave(path, TimeSpan.FromMilliseconds(500));
            var duration = WaveDurationReader.Measure(path);
            return Task.FromResult(new NarrationSynthesisResult(
                request.SegmentId,
                path,
                duration,
                NarrationTimingPolicy.Evaluate(
                    duration,
                    request.WindowDuration,
                    request.BreathingRoom,
                    request.AutoFitAttempt),
                false,
                voice));
        }
    }

    private sealed class ProjectFixture : IDisposable
    {
        private ProjectFixture(string root, Guid projectId, FileProjectVersionStore store)
        {
            Root = root;
            ProjectId = projectId;
            Store = store;
            ProjectDirectory = Path.Combine(root, projectId.ToString("D"));
        }

        public string Root { get; }
        public Guid ProjectId { get; }
        public string ProjectDirectory { get; }
        public FileProjectVersionStore Store { get; }

        public static async Task<ProjectFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ADFi.Tests", Guid.NewGuid().ToString("N"));
            var projectId = Guid.NewGuid();
            var store = new FileProjectVersionStore(root);
            var fixture = new ProjectFixture(root, projectId, store);
            Directory.CreateDirectory(Path.Combine(fixture.ProjectDirectory, "manifests"));
            var sourceHash = new string('A', 64);
            var interval = new MediaInterval(TimeSpan.Zero, TimeSpan.FromSeconds(5));
            var frame = new KeyframeEvidence(
                "frame-1", TimeSpan.FromSeconds(1), "visual/frames/frame-1.jpg", 640, 360, 0, 0, false);
            var segment = new AdSegment(
                "ad-1",
                "scene-00000",
                new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(4500)),
                "A woman slowly places a brass key on the wooden table.",
                ["frame-1"],
                "ADFI_AD_WRITER_SYSTEM",
                "1.0.0",
                new string('B', 64),
                "narration/cache/original.wav",
                TimeSpan.FromMilliseconds(500),
                false,
                new string('C', 64));
            var originalAudio = Path.Combine(fixture.ProjectDirectory, "narration", "cache", "original.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(originalAudio)!);
            WriteWave(originalAudio, TimeSpan.FromMilliseconds(500));
            await store.InitializeAsync(
                new(projectId, 1, sourceHash, interval.Duration, [segment], "voice-1", DateTimeOffset.UnixEpoch, null),
                TestContext.Current.CancellationToken);
            var audio = new AudioAnalysisCompletionManifest(
                projectId,
                sourceHash,
                "tiny",
                new(
                    projectId,
                    sourceHash,
                    "audio/analysis.wav",
                    new(16_000, 1, null, null, []),
                    [],
                    DateTimeOffset.UnixEpoch),
                [],
                [new(interval, SourceAudioKind.QuietCandidate, 1m, "quiet-1")],
                [new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(4500))],
                DateTimeOffset.UnixEpoch);
            var visual = new VisualIndexManifest(
                projectId,
                sourceHash,
                TimeSpan.FromSeconds(1),
                640,
                3,
                [new("scene-00000", interval, [frame], [], ["quiet-1"])],
                DateTimeOffset.UnixEpoch);
            var provenance = new PromptDecisionProvenance(
                "ADFI_VISUAL_ANALYST_SYSTEM",
                "1.0.0",
                new string('D', 64),
                "test",
                "qwen-test",
                DateTimeOffset.UnixEpoch);
            var analysis = new VisualUnderstandingManifest(
                projectId,
                sourceHash,
                [new(
                    "scene-00000",
                    [new("observation-1", "action", "A woman places a brass key on a wooden table.", ["frame-1"], 0.98m, 0.9m, false)],
                    [],
                    false,
                    false,
                    null,
                    provenance)],
                DateTimeOffset.UnixEpoch);
            var text = new VisualTextAnalysisManifest(projectId, sourceHash, [], DateTimeOffset.UnixEpoch);
            var faces = new FaceContinuityManifest(projectId, sourceHash, [], [], [], DateTimeOffset.UnixEpoch);
            await WriteAsync(fixture.ProjectDirectory, "audio-analysis-complete.json", audio);
            await WriteAsync(fixture.ProjectDirectory, "visual-index.json", visual);
            await WriteAsync(fixture.ProjectDirectory, "visual-analysis-complete.json", analysis);
            await WriteAsync(fixture.ProjectDirectory, "visual-text-analysis.json", text);
            await WriteAsync(fixture.ProjectDirectory, "face-continuity.json", faces);
            return fixture;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static async Task WriteAsync<T>(string project, string name, T value)
        {
            var path = Path.Combine(project, "manifests", name);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(value),
                TestContext.Current.CancellationToken);
        }
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
}
