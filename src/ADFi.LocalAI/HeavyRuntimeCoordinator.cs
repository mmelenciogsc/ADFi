namespace ADFi.LocalAI;

public enum HeavyRuntimeKind
{
    Whisper,
    VisionLanguageModel,
    Piper,
}

public sealed record ResourceSnapshot(long FreeSystemMemoryBytes, long FreeVideoMemoryBytes);

public interface IResourceSnapshotProvider
{
    Task<ResourceSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

public interface IHeavyRuntimeController
{
    Task UnloadAsync(HeavyRuntimeKind runtime, CancellationToken cancellationToken);
}

public sealed class HeavyRuntimeCoordinator(
    IResourceSnapshotProvider snapshots,
    IHeavyRuntimeController controller) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public async Task<IAsyncDisposable> AcquireAsync(
        HeavyRuntimeKind runtime,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await snapshots.CaptureAsync(cancellationToken).ConfigureAwait(false);
            return new RuntimeLease(this, runtime, before);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask ReleaseAsync(
        HeavyRuntimeKind runtime,
        ResourceSnapshot before)
    {
        try
        {
            await controller.UnloadAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var after = await snapshots.CaptureAsync(CancellationToken.None).ConfigureAwait(false);
            if (after.FreeSystemMemoryBytes < Math.Min(before.FreeSystemMemoryBytes / 2, 512L * 1024 * 1024))
            {
                throw new InvalidOperationException($"Resource reclamation could not be confirmed after unloading {runtime}.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class RuntimeLease(
        HeavyRuntimeCoordinator owner,
        HeavyRuntimeKind runtime,
        ResourceSnapshot before) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref released, 1) == 0
                ? owner.ReleaseAsync(runtime, before)
                : ValueTask.CompletedTask;
    }
}

