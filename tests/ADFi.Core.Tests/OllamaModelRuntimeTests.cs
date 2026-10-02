using System.Net;
using System.Text;
using System.Text.Json;
using ADFi.LocalAI;

namespace ADFi.Core.Tests;

public sealed class OllamaModelRuntimeTests
{
    [Fact]
    public async Task GenerateSendsStructuredSchemaAsJsonObject()
    {
        string? requestBody = null;
        using var client = new HttpClient(new StubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return JsonResponse(HttpStatusCode.OK, """{"model":"qwen-test","message":{"content":"{}"}}""");
        }))
        {
            BaseAddress = new Uri("http://localhost:11434/"),
        };
        await using var runtime = new OllamaModelRuntime(client);

        var result = await runtime.GenerateAsync(
            "qwen-test",
            new("system", "user", [], """{"type":"object","properties":{"answer":{"type":"string"}}}""", 128),
            TestContext.Current.CancellationToken);

        Assert.Equal("{}", result.Content);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        Assert.Equal(JsonValueKind.Object, payload.RootElement.GetProperty("format").ValueKind);
        Assert.Equal("object", payload.RootElement.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(8_192, payload.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task GenerateReportsBoundedOllamaErrorDetail()
    {
        using var client = new HttpClient(new StubHandler(_ => Task.FromResult(
            JsonResponse(HttpStatusCode.InternalServerError, """{"error":"model runner rejected the response schema"}"""))))
        {
            BaseAddress = new Uri("http://localhost:11434/"),
        };
        await using var runtime = new OllamaModelRuntime(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.GenerateAsync(
            "qwen-test",
            new("secret system prompt", "secret user prompt", [], "json", 128),
            TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 500", exception.Message, StringComparison.Ordinal);
        Assert.Contains("model runner rejected the response schema", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content) =>
        new(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request);
    }
}
