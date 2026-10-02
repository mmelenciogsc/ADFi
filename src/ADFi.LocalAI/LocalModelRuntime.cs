using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ADFi.LocalAI;

public sealed record LocalModelRequest(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<string> ImagePaths,
    string RequiredJsonSchema,
    int MaximumOutputTokens);

public sealed record LocalModelResponse(string Content, string Model, TimeSpan Elapsed);

public interface ILocalModelRuntime : IAsyncDisposable
{
    string RuntimeName { get; }

    Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken);

    Task<LocalModelResponse> GenerateAsync(
        string modelName,
        LocalModelRequest request,
        CancellationToken cancellationToken);

    Task UnloadAsync(string modelName, CancellationToken cancellationToken);
}

public sealed class OllamaModelRuntime(HttpClient httpClient) : ILocalModelRuntime
{
    public string RuntimeName => "Ollama";

    public async Task EnsureModelAvailableAsync(string modelName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        using var response = await httpClient.PostAsJsonAsync(
            "api/show",
            new { model = modelName },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<LocalModelResponse> GenerateAsync(
        string modelName,
        LocalModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var responseFormat = ParseResponseFormat(request.RequiredJsonSchema);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var images = new List<string>(request.ImagePaths.Count);
        foreach (var imagePath in request.ImagePaths)
        {
            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            images.Add(Convert.ToBase64String(bytes));
        }

        var payload = new
        {
            model = modelName,
            stream = false,
            keep_alive = 0,
            // Ollama requires a JSON schema object here. Sending the serialized schema as a
            // JSON string causes some releases to fail the request with an opaque HTTP 500.
            format = responseFormat,
            // Vision tokens plus the structured prompt can exhaust Ollama's small default
            // context before the JSON object closes. Eight thousand tokens remains bounded
            // while leaving enough room for the requested response on the supported models.
            options = new { num_ctx = 8_192, num_predict = request.MaximumOutputTokens, temperature = 0 },
            messages = new object[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt, images },
            },
        };
        using var response = await httpClient.PostAsJsonAsync(
            "api/chat",
            payload,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Ollama chat request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}");
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Ollama returned an empty response.");
        started.Stop();
        return new(result.Message.Content, result.Model, started.Elapsed);
    }

    public async Task UnloadAsync(string modelName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/generate",
            new { model = modelName, keep_alive = 0 },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private static object ParseResponseFormat(string requiredJsonSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredJsonSchema);
        var value = requiredJsonSchema.Trim();
        if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            return "json";
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "The required Ollama response format must be 'json' or a JSON schema object.",
                    nameof(requiredJsonSchema));
            }

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The required Ollama response format is not valid JSON.",
                nameof(requiredJsonSchema),
                exception);
        }
    }

    private static async Task<string> ReadErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int MaximumDetailCharacters = 1_000;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = body;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                detail = error.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Preserve a bounded plain-text server diagnostic when the response is not JSON.
        }

        detail = string.Concat(detail.Where(character => !char.IsControl(character) || character == ' ')).Trim();
        if (detail.Length > MaximumDetailCharacters)
        {
            detail = detail[..MaximumDetailCharacters] + "…";
        }

        return string.IsNullOrEmpty(detail) ? "Ollama returned no error detail." : detail;
    }

    private sealed record OllamaChatResponse(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("message")] OllamaMessage Message);

    private sealed record OllamaMessage(
        [property: JsonPropertyName("content")] string Content);
}
