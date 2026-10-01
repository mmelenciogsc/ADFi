using ADFi.LocalAI;

namespace ADFi.Core.Tests;

public sealed class HeavyRuntimeCoordinatorTests
{
    [Fact]
    public async Task HeavyRuntimesCannotOverlapAndUnloadBeforeNextLease()
    {
        var controller = new RecordingController();
        await using var coordinator = new HeavyRuntimeCoordinator(new StableSnapshots(), controller);
        await using var first = await coordinator.AcquireAsync(
            HeavyRuntimeKind.Whisper,
            TestContext.Current.CancellationToken);
        var secondTask = coordinator.AcquireAsync(
            HeavyRuntimeKind.VisionLanguageModel,
            TestContext.Current.CancellationToken);

        Assert.False(secondTask.IsCompleted);
        await first.DisposeAsync();
        await using var second = await secondTask;

        Assert.Equal([HeavyRuntimeKind.Whisper], controller.Unloaded);
        await second.DisposeAsync();
        Assert.Equal(
            [HeavyRuntimeKind.Whisper, HeavyRuntimeKind.VisionLanguageModel],
            controller.Unloaded);
    }

    private sealed class StableSnapshots : IResourceSnapshotProvider
    {
        public Task<ResourceSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot(8L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024));
    }

    private sealed class RecordingController : IHeavyRuntimeController
    {
        public List<HeavyRuntimeKind> Unloaded { get; } = [];

        public Task UnloadAsync(HeavyRuntimeKind runtime, CancellationToken cancellationToken)
        {
            Unloaded.Add(runtime);
            return Task.CompletedTask;
        }
    }
}
