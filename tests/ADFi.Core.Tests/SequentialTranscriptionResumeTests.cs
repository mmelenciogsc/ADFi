using System.Text.Json;
using ADFi.Core;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class SequentialTranscriptionResumeTests
{
    [Fact]
    public async Task CompletedChunksAreRestoredAndWhisperProcessesOnlyMissingChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "ADFi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "audio", "chunks"));
        Directory.CreateDirectory(Path.Combine(root, "transcript", "chunks"));
        var chunks = new[]
        {
            new AudioChunk("audio-00000", new(TimeSpan.Zero, TimeSpan.FromSeconds(10)), "audio/chunks/audio-00000.wav", 16_000, 1),
            new AudioChunk("audio-00001", new(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(20)), "audio/chunks/audio-00001.wav", 16_000, 1),
        };
        foreach (var chunk in chunks)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(root, chunk.RelativeAudioPath.Replace('/', Path.DirectorySeparatorChar)),
                [0],
                TestContext.Current.CancellationToken);
        }

        var restored = new[]
        {
            Segment(chunks[0], "restored", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "transcript", "chunks", "audio-00000.json"),
            JsonSerializer.Serialize(restored),
            TestContext.Current.CancellationToken);

        try
        {
            var runtime = new RecordingWhisperRuntime();
            await using var serviceRuntime = runtime;
            var progress = new RecordingProgress();
            var service = new SequentialTranscriptionService(runtime, TimeProvider.System, progress);
            var result = await service.TranscribeAsync(
                new(
                    Guid.NewGuid(),
                    "source-hash",
                    "audio/analysis-mono-16k.wav",
                    new(16_000, 1, null, null, []),
                    chunks,
                    DateTimeOffset.UtcNow),
                root,
                "tiny",
                TimeSpan.FromSeconds(20),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, result.Count);
            Assert.Equal(1, runtime.LoadCount);
            Assert.Equal(["audio-00001"], runtime.TranscribedChunkIds);
            Assert.Equal(1, runtime.UnloadCount);
            Assert.Contains(progress.Items, item => item.Announcement.StartsWith("Restored transcript chunk 1", StringComparison.Ordinal));

            var noWorkRuntime = new RecordingWhisperRuntime();
            await using var noWorkServiceRuntime = noWorkRuntime;
            var resumed = await new SequentialTranscriptionService(noWorkRuntime, TimeProvider.System)
                .TranscribeAsync(
                    new(
                        Guid.NewGuid(),
                        "source-hash",
                        "audio/analysis-mono-16k.wav",
                        new(16_000, 1, null, null, []),
                        chunks,
                        DateTimeOffset.UtcNow),
                    root,
                    "tiny",
                    TimeSpan.FromSeconds(20),
                    TestContext.Current.CancellationToken);

            Assert.Equal(2, resumed.Count);
            Assert.Equal(0, noWorkRuntime.LoadCount);
            Assert.Empty(noWorkRuntime.TranscribedChunkIds);
            Assert.Equal(0, noWorkRuntime.UnloadCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TranscriptSegment Segment(
        AudioChunk chunk,
        string text,
        TimeSpan start,
        TimeSpan end) =>
        new($"{chunk.ChunkId}-segment", chunk.ChunkId, new(start, end), text, 0.9m, []);

    private sealed class RecordingWhisperRuntime : IWhisperRuntime
    {
        public int LoadCount { get; private set; }

        public int UnloadCount { get; private set; }

        public List<string> TranscribedChunkIds { get; } = [];

        public Task LoadAsync(string modelName, CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioChunk chunk,
            string absoluteChunkPath,
            CancellationToken cancellationToken)
        {
            TranscribedChunkIds.Add(chunk.ChunkId);
            IReadOnlyList<TranscriptSegment> result =
            [
                Segment(
                    chunk,
                    "new",
                    chunk.SourceInterval.Start + TimeSpan.FromSeconds(1),
                    chunk.SourceInterval.Start + TimeSpan.FromSeconds(2)),
            ];
            return Task.FromResult(result);
        }

        public Task UnloadAsync(CancellationToken cancellationToken)
        {
            UnloadCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingProgress : IProgress<TranscriptionProgress>
    {
        public List<TranscriptionProgress> Items { get; } = [];

        public void Report(TranscriptionProgress value) => Items.Add(value);
    }
}
