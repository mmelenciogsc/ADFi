using System.Net.Http.Json;
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
            format = request.RequiredJsonSchema,
            options = new { num_predict = request.MaximumOutputTokens, temperature = 0 },
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
        response.EnsureSuccessStatusCode();
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

    private sealed record OllamaChatResponse(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("message")] OllamaMessage Message);

    private sealed record OllamaMessage(
        [property: JsonPropertyName("content")] string Content);
}

