using System.Text.Json;
using ADFi.Core;

namespace ADFi.Media;

public interface IWhisperRuntime : IAsyncDisposable
{
    Task LoadAsync(string modelName, CancellationToken cancellationToken);

    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        AudioChunk chunk,
        string absoluteChunkPath,
        CancellationToken cancellationToken);

    Task UnloadAsync(CancellationToken cancellationToken);
}

public sealed record TranscriptionProgress(
    int CompletedChunks,
    int TotalChunks,
    string Announcement);

public sealed class SequentialTranscriptionService(
    IWhisperRuntime whisperRuntime,
    TimeProvider timeProvider,
    IProgress<TranscriptionProgress>? progress = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        AudioPreparationManifest manifest,
        string projectDirectory,
        string modelName,
        TimeSpan mediaDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var segments = new List<TranscriptSegment>();
        var pending = new List<(AudioChunk Chunk, int Index)>();
        for (var index = 0; index < manifest.Chunks.Count; index++)
        {
            var chunk = manifest.Chunks[index];
            var restored = await TryLoadChunkResultAsync(projectDirectory, chunk, cancellationToken)
                .ConfigureAwait(false);
            if (restored is null)
            {
                pending.Add((chunk, index));
            }
            else
            {
                segments.AddRange(restored);
                progress?.Report(new(
                    index + 1,
                    manifest.Chunks.Count,
                    $"Restored transcript chunk {index + 1} of {manifest.Chunks.Count}."));
            }
        }

        var loaded = false;
        try
        {
            if (pending.Count > 0)
            {
                await whisperRuntime.LoadAsync(modelName, cancellationToken).ConfigureAwait(false);
                loaded = true;
            }

            foreach (var pendingChunk in pending)
            {
                var chunk = pendingChunk.Chunk;
                cancellationToken.ThrowIfCancellationRequested();
                var chunkPath = Path.Combine(
                    Path.GetFullPath(projectDirectory),
                    chunk.RelativeAudioPath.Replace('/', Path.DirectorySeparatorChar));
                var result = await whisperRuntime.TranscribeAsync(chunk, chunkPath, cancellationToken)
                    .ConfigureAwait(false);
                segments.AddRange(result);
                await PersistChunkResultAsync(
                    projectDirectory,
                    chunk.ChunkId,
                    result,
                    cancellationToken).ConfigureAwait(false);
                progress?.Report(new(
                    pendingChunk.Index + 1,
                    manifest.Chunks.Count,
                    $"Transcribing chunk {pendingChunk.Index + 1} of {manifest.Chunks.Count} complete."));
            }
        }
        finally
        {
            if (loaded)
            {
                await whisperRuntime.UnloadAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        var merged = TranscriptMerger.Merge(segments, mediaDuration);
        var finalPath = Path.Combine(projectDirectory, "manifests", "transcription-complete.json");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        await WriteJsonAtomicallyAsync(
            finalPath,
            new
            {
                manifest.ProjectId,
                Model = modelName,
                CompletedAtUtc = timeProvider.GetUtcNow(),
                Segments = merged,
            },
            cancellationToken).ConfigureAwait(false);
        return merged;
    }

    private static async Task<IReadOnlyList<TranscriptSegment>?> TryLoadChunkResultAsync(
        string projectDirectory,
        AudioChunk chunk,
        CancellationToken cancellationToken)
    {
        var path = ChunkResultPath(projectDirectory, chunk.ChunkId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var segments = await JsonSerializer.DeserializeAsync<List<TranscriptSegment>>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (segments is null || segments.Any(segment =>
                    !string.Equals(segment.ChunkId, chunk.ChunkId, StringComparison.Ordinal)
                    || segment.SourceInterval.Start < chunk.SourceInterval.Start
                    || segment.SourceInterval.End > chunk.SourceInterval.End
                    || segment.SourceInterval.End <= segment.SourceInterval.Start))
            {
                return null;
            }

            return segments;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task PersistChunkResultAsync(
        string projectDirectory,
        string chunkId,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(
            ChunkResultPath(projectDirectory, chunkId),
            segments,
            cancellationToken);

    private static string ChunkResultPath(string projectDirectory, string chunkId) =>
        Path.Combine(projectDirectory, "transcript", "chunks", $"{chunkId}.json");

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, true);
    }
}
