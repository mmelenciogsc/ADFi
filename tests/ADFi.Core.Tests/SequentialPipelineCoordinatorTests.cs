using ADFi.Core;

namespace ADFi.Core.Tests;

public sealed class SequentialPipelineCoordinatorTests
{
    [Fact]
    public async Task ResumeSkipsVerifiedCompletePhaseAndRunsRemainingWorkInOrder()
    {
        var projectId = Guid.NewGuid();
        var store = new MemoryCheckpointStore(
            new PipelineCheckpoint(projectId, AdficationPhase.SourceIntakeAndAuthorization, CheckpointStatus.Complete,
                DateTimeOffset.UnixEpoch, "source.json", "abc", null));
        var calls = new List<AdficationPhase>();
        var coordinator = new SequentialPipelineCoordinator(
            [
                new RecordingPhase(AdficationPhase.SourceIntakeAndAuthorization, calls),
                new RecordingPhase(AdficationPhase.AudioCharacterizationAndDialogueSegmentation, calls),
                new RecordingPhase(AdficationPhase.VisualSegmentationAndKeyframes, calls),
            ],
            store,
            new FixedTimeProvider());

        await coordinator.RunAsync(projectId, TestContext.Current.CancellationToken);

        Assert.Equal(
            [AdficationPhase.AudioCharacterizationAndDialogueSegmentation, AdficationPhase.VisualSegmentationAndKeyframes],
            calls);
        Assert.Equal(CheckpointStatus.Complete, store.Items[AdficationPhase.VisualSegmentationAndKeyframes].Status);
    }

    [Fact]
    public async Task FailureIsCheckpointedAndLaterPhaseDoesNotRun()
    {
        var projectId = Guid.NewGuid();
        var store = new MemoryCheckpointStore();
        var calls = new List<AdficationPhase>();
        var coordinator = new SequentialPipelineCoordinator(
            [
                new RecordingPhase(AdficationPhase.SourceIntakeAndAuthorization, calls, shouldFail: true),
                new RecordingPhase(AdficationPhase.AudioCharacterizationAndDialogueSegmentation, calls),
            ],
            store,
            new FixedTimeProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunAsync(projectId, TestContext.Current.CancellationToken));

        Assert.Equal([AdficationPhase.SourceIntakeAndAuthorization], calls);
        var failed = store.Items[AdficationPhase.SourceIntakeAndAuthorization];
        Assert.Equal(CheckpointStatus.Failed, failed.Status);
        Assert.Equal("SOURCEINTAKEANDAUTHORIZATION_FAILED", failed.FailureCode);
    }

    [Fact]
    public async Task ProgressUsesConciseScreenReaderAnnouncements()
    {
        var progress = new RecordingProgress();
        var coordinator = new SequentialPipelineCoordinator(
            [new RecordingPhase(AdficationPhase.InitialPreviewAssembly, [])],
            new MemoryCheckpointStore(),
            new FixedTimeProvider(),
            progress);

        await coordinator.RunAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal("Starting InitialPreviewAssembly.", progress.Items[0].Announcement);
        Assert.Equal("Project ready for review.", progress.Items[1].Announcement);
    }

    [Fact]
    public async Task InvalidEarlierCheckpointForcesAllDependentPhasesToRerun()
    {
        var projectId = Guid.NewGuid();
        var store = new MemoryCheckpointStore(
            new(projectId, AdficationPhase.AdNecessityAndNarrationWindows, CheckpointStatus.Complete,
                DateTimeOffset.UnixEpoch, "opportunities.json", "old", null),
            new(projectId, AdficationPhase.DraftGeneration, CheckpointStatus.Complete,
                DateTimeOffset.UnixEpoch, "draft.json", "valid", null));
        var calls = new List<AdficationPhase>();
        var coordinator = new SequentialPipelineCoordinator(
            [
                new VerifyingPhase(AdficationPhase.AdNecessityAndNarrationWindows, calls, isValid: false),
                new VerifyingPhase(AdficationPhase.DraftGeneration, calls, isValid: true),
            ],
            store,
            new FixedTimeProvider());

        await coordinator.RunAsync(projectId, TestContext.Current.CancellationToken);

        Assert.Equal(
            [AdficationPhase.AdNecessityAndNarrationWindows, AdficationPhase.DraftGeneration],
            calls);
    }

    private sealed class RecordingPhase(
        AdficationPhase phase,
        List<AdficationPhase> calls,
        bool shouldFail = false) : IPipelinePhase
    {
        public AdficationPhase Phase => phase;

        public Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            calls.Add(phase);
            if (shouldFail)
            {
                throw new InvalidOperationException("Expected test failure.");
            }

            return Task.FromResult(new PipelineCheckpoint(
                projectId, phase, CheckpointStatus.Complete, DateTimeOffset.UnixEpoch,
                $"{phase}.json", "hash", null));
        }
    }

    private sealed class VerifyingPhase(
        AdficationPhase phase,
        List<AdficationPhase> calls,
        bool isValid) : IVerifiedPipelinePhase
    {
        public AdficationPhase Phase => phase;

        public Task<bool> VerifyCheckpointAsync(
            PipelineCheckpoint checkpoint,
            CancellationToken cancellationToken) => Task.FromResult(isValid);

        public Task<PipelineCheckpoint> ExecuteAsync(Guid projectId, CancellationToken cancellationToken)
        {
            calls.Add(phase);
            return Task.FromResult(new PipelineCheckpoint(
                projectId, phase, CheckpointStatus.Complete, DateTimeOffset.UnixEpoch,
                $"{phase}.json", "new", null));
        }
    }

    private sealed class MemoryCheckpointStore(params PipelineCheckpoint[] initial) : IPipelineCheckpointStore
    {
        public Dictionary<AdficationPhase, PipelineCheckpoint> Items { get; } =
            initial.ToDictionary(item => item.Phase);

        public Task<IReadOnlyList<PipelineCheckpoint>> LoadAsync(
            Guid projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PipelineCheckpoint>>(
                Items.Values.Where(item => item.ProjectId == projectId).ToArray());

        public Task SaveAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Items[checkpoint.Phase] = checkpoint;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProgress : IProgress<PipelineProgress>
    {
        public List<PipelineProgress> Items { get; } = [];

        public void Report(PipelineProgress value) => Items.Add(value);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
