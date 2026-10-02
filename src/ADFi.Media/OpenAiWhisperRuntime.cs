using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADFi.Core;

namespace ADFi.Media;

public sealed record WhisperRuntimeConfiguration(
    string PythonExecutable,
    string WorkerScriptPath,
    string ModelDirectory,
    string Device = "cpu");

public static class WhisperModelPolicy
{
    public static string Select(long totalSystemMemoryBytes) => totalSystemMemoryBytes switch
    {
        >= 16L * 1024 * 1024 * 1024 => "small",
        >= 8L * 1024 * 1024 * 1024 => "base",
        _ => "tiny",
    };
}

/// <summary>
/// Keeps one official openai-whisper model resident while bounded chunks are transcribed, then
/// explicitly asks the worker to release it. CPU is the default to reserve VRAM for Qwen3-VL.
/// </summary>
public sealed class OpenAiWhisperRuntime(WhisperRuntimeConfiguration configuration) : IWhisperRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim commandLock = new(1, 1);
    private Process? process;
    private Task<string>? stderrTask;
    private string? loadedModel;

    public async Task LoadAsync(string modelName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (loadedModel is not null)
        {
            throw new InvalidOperationException("A Whisper model is already loaded.");
        }

        if (!File.Exists(configuration.WorkerScriptPath))
        {
            throw new FileNotFoundException("The ADFi Whisper worker is missing.", configuration.WorkerScriptPath);
        }

        Directory.CreateDirectory(configuration.ModelDirectory);
        var startInfo = new ProcessStartInfo(configuration.PythonExecutable)
        {
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.GetFullPath(configuration.WorkerScriptPath));
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(configuration.Device);
        startInfo.ArgumentList.Add("--download-root");
        startInfo.ArgumentList.Add(Path.GetFullPath(configuration.ModelDirectory));
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The OpenAI Whisper worker could not start.");
        }

        // Drain stderr for the worker lifetime so verbose model output cannot fill the pipe.
        // Do not bind this task to an individual command's cancellation token.
        stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var response = await SendAsync(new { type = "load", model = modelName }, cancellationToken)
                .ConfigureAwait(false);
            EnsureSuccessful(response, "WHISPER_LOAD_FAILED");
            loadedModel = modelName;
        }
        catch
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        AudioChunk chunk,
        string absoluteChunkPath,
        CancellationToken cancellationToken)
    {
        if (loadedModel is null || process is null)
        {
            throw new InvalidOperationException("Whisper is not loaded.");
        }

        if (!File.Exists(absoluteChunkPath))
        {
            throw new FileNotFoundException("Whisper audio chunk is missing.", absoluteChunkPath);
        }

        var response = await SendAsync(
            new { type = "transcribe", path = Path.GetFullPath(absoluteChunkPath) },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessful(response, "WHISPER_TRANSCRIPTION_FAILED");
        var payload = response.Deserialize<WorkerResponse>(JsonOptions)
                      ?? throw new InvalidDataException("Whisper worker returned no transcription payload.");
        return (payload.Segments ?? [])
            .Select((segment, index) => TryMapSegment(chunk, segment, index))
            .OfType<TranscriptSegment>()
            .ToArray();
    }

    public async Task UnloadAsync(CancellationToken cancellationToken)
    {
        if (process is null)
        {
            loadedModel = null;
            return;
        }

        try
        {
            var unload = await SendAsync(new { type = "unload" }, cancellationToken).ConfigureAwait(false);
            EnsureSuccessful(unload, "WHISPER_UNLOAD_FAILED");
            var exit = await SendAsync(new { type = "exit" }, cancellationToken).ConfigureAwait(false);
            EnsureSuccessful(exit, "WHISPER_EXIT_FAILED");
        }
        finally
        {
            loadedModel = null;
            await StopProcessAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessAsync().ConfigureAwait(false);
        commandLock.Dispose();
    }

    private async Task<JsonElement> SendAsync(object command, CancellationToken cancellationToken)
    {
        await commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is null || process.HasExited)
            {
                throw new InvalidOperationException("The Whisper worker is not running.");
            }

            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                var error = stderrTask is null ? string.Empty : await stderrTask.ConfigureAwait(false);
                throw new InvalidOperationException($"Whisper worker stopped unexpectedly: {Sanitize(error)}");
            }

            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        finally
        {
            commandLock.Release();
        }
    }

    private async Task StopProcessAsync()
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
            process = null;
            stderrTask = null;
        }
    }

    private static TranscriptSegment? TryMapSegment(AudioChunk chunk, WorkerSegment segment, int index)
    {
        if (!double.IsFinite(segment.StartSeconds)
            || !double.IsFinite(segment.EndSeconds)
            || string.IsNullOrWhiteSpace(segment.Text))
        {
            return null;
        }

        var start = chunk.SourceInterval.Start + TimeSpan.FromSeconds(segment.StartSeconds);
        var end = chunk.SourceInterval.Start + TimeSpan.FromSeconds(segment.EndSeconds);
        start = Max(start, chunk.SourceInterval.Start);
        end = Min(end, chunk.SourceInterval.End);
        if (end <= start)
        {
            // Whisper can return a zero-length tail segment or timestamps just beyond the decoded
            // chunk boundary. That output carries no usable speech evidence, so discard only that
            // segment instead of losing already-persisted and later valid chunks.
            return null;
        }

        var words = (segment.Words ?? [])
            .Select(word => new
            {
                Text = word.Text.Trim(),
                Start = Max(chunk.SourceInterval.Start + TimeSpan.FromSeconds(word.StartSeconds), start),
                End = Min(chunk.SourceInterval.Start + TimeSpan.FromSeconds(word.EndSeconds), end),
                word.Confidence,
            })
            .Where(word => word.Text.Length > 0 && word.End > word.Start)
            .Select(word => new TranscriptWord(word.Text, word.Start, word.End, word.Confidence))
            .ToArray();
        return new(
            $"{chunk.ChunkId}-segment-{index:D4}",
            chunk.ChunkId,
            new(start, end),
            segment.Text,
            segment.Confidence,
            words);
    }

    private static void EnsureSuccessful(JsonElement response, string code)
    {
        if (!response.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = response.TryGetProperty("error", out var value) ? value.GetString() : "Unknown worker error.";
            throw new InvalidOperationException($"{code}: {error}");
        }
    }

    private static string Sanitize(string value)
    {
        var result = string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return result[..Math.Min(result.Length, 500)];
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private sealed record WorkerResponse(
        [property: JsonPropertyName("segments")] IReadOnlyList<WorkerSegment>? Segments);

    private sealed record WorkerSegment(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("startSeconds")] double StartSeconds,
        [property: JsonPropertyName("endSeconds")] double EndSeconds,
        [property: JsonPropertyName("confidence")] decimal? Confidence,
        [property: JsonPropertyName("words")] IReadOnlyList<WorkerWord>? Words);

    private sealed record WorkerWord(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("startSeconds")] double StartSeconds,
        [property: JsonPropertyName("endSeconds")] double EndSeconds,
        [property: JsonPropertyName("confidence")] decimal? Confidence);
}
