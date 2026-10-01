using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ADFi.LocalAI;

/// <summary>
/// Measures one deliberately small, non-visual generation after the selected model package is
/// installed. Ollama is told to unload the model immediately so profiling cannot leave VRAM
/// occupied before media processing begins.
/// </summary>
public sealed class OllamaInferenceBenchmark(HttpClient httpClient, string modelName)
    : IMiniatureInferenceBenchmark
{
    public async Task<decimal> MeasureTokensPerSecondAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        using var availability = await httpClient.PostAsJsonAsync(
            "api/show",
            new { model = modelName },
            cancellationToken).ConfigureAwait(false);
        if (!availability.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama model '{modelName}' is not installed.");
        }

        var timer = Stopwatch.StartNew();
        using var response = await httpClient.PostAsJsonAsync(
            "api/generate",
            new
            {
                model = modelName,
                prompt = "Reply with exactly these words: ADFi local benchmark complete.",
                stream = false,
                keep_alive = 0,
                options = new { temperature = 0, num_predict = 16 },
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama benchmark failed with HTTP {(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync<GenerateResult>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Ollama returned no benchmark measurements.");
        timer.Stop();

        var generationSeconds = result.EvaluationDurationNanoseconds > 0
            ? result.EvaluationDurationNanoseconds / 1_000_000_000m
            : (decimal)timer.Elapsed.TotalSeconds;
        if (result.EvaluationCount <= 0 || generationSeconds <= 0)
        {
            throw new InvalidOperationException("Ollama returned incomplete benchmark measurements.");
        }

        return decimal.Round(result.EvaluationCount / generationSeconds, 2);
    }

    private sealed record GenerateResult(
        [property: JsonPropertyName("eval_count")] int EvaluationCount,
        [property: JsonPropertyName("eval_duration")] long EvaluationDurationNanoseconds);
}
