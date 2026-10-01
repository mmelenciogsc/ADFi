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

public sealed class SequentialTranscriptionService(
    IWhisperRuntime whisperRuntime,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        AudioPreparationManifest manifest,
        string projectDirectory,
        string modelName,
        TimeSpan mediaDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await whisperRuntime.LoadAsync(modelName, cancellationToken).ConfigureAwait(false);
        var segments = new List<TranscriptSegment>();
        try
        {
            foreach (var chunk in manifest.Chunks)
            {
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
            }
        }
        finally
        {
            await whisperRuntime.UnloadAsync(CancellationToken.None).ConfigureAwait(false);
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

    private static Task PersistChunkResultAsync(
        string projectDirectory,
        string chunkId,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(
            Path.Combine(projectDirectory, "transcript", "chunks", $"{chunkId}.json"),
            segments,
            cancellationToken);

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
