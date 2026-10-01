using System.Net;
using System.Text;
using ADFi.LocalAI;

namespace ADFi.Core.Tests;

public sealed class OllamaInferenceBenchmarkTests
{
    [Fact]
    public async Task UsesOllamaMeasurementsAndRequestsImmediateUnload()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        var benchmark = new OllamaInferenceBenchmark(client, ModelTierPolicy.BalancedQwen3Vl4B);

        var rate = await benchmark.MeasureTokensPerSecondAsync(TestContext.Current.CancellationToken);

        Assert.Equal(20m, rate);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("\"keep_alive\":0", handler.Bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("image", handler.Bodies[1], StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var json = request.RequestUri?.AbsolutePath.EndsWith("/show", StringComparison.Ordinal) == true
                ? "{}"
                : "{\"eval_count\":10,\"eval_duration\":500000000}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
