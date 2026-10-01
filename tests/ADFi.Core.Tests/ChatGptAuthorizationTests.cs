using ADFi.Infrastructure.CloudAI;
using ADFi.Core;
using System.Net;
using System.Text;

namespace ADFi.Core.Tests;

public sealed class ChatGptAuthorizationTests
{
    [Fact]
    public void InitialAuthorizationUsesLoopbackPkceAndExactPlanScopes()
    {
        var attempt = ChatGptAuthorization.CreateAttempt(
            Guid.Parse("6b050222-9f45-4e15-a56e-25359083e872"),
            1455,
            TimeProvider.System);
        var uri = ChatGptAuthorization.BuildAuthorizationUri(attempt);

        Assert.Equal("auth.openai.com", uri.Host);
        Assert.Contains("client_id=dynamic_agent_client", uri.Query, StringComparison.Ordinal);
        Assert.Contains("agent_name_hint=ADFi", uri.Query, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", uri.Query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(ChatGptAuthorization.ApiResource), uri.Query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(ChatGptAuthorization.PlanUsageScope), uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CallbackRejectsStateMismatch()
    {
        var attempt = ChatGptAuthorization.CreateAttempt(Guid.NewGuid(), 1455, TimeProvider.System);
        var callback = new ChatGptAuthorizationCallback(
            "code",
            "wrong-state",
            "oaiapp_example",
            new HashSet<string>([ChatGptAuthorization.PlanUsageScope], StringComparer.Ordinal));

        Assert.Throws<InvalidOperationException>(() => ChatGptAuthorization.ValidateCallback(attempt, callback));
    }

    [Fact]
    public void IdentityWithoutGrantedPlanScopeCannotInvokePlanUsage()
    {
        Assert.False(ChatGptAuthorization.GrantsPlanUsage(
            new HashSet<string>(["openid", "profile", "email"], StringComparer.Ordinal)));
    }

    [Fact]
    public async Task PlanVqaUsesPublicResponsesWithStreamingAndNoStorage()
    {
        var image = Path.Combine(Path.GetTempPath(), $"adfi-chatgpt-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(image, [137, 80, 78, 71], TestContext.Current.CancellationToken);
        try
        {
            var handler = new PlanUsageHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
            var provider = new ChatGptMomentVqaProvider(
                new FixedCredentialSource(),
                new ChatGptPlanResponsesClient(http),
                TimeProvider.System);
            var result = await provider.AskAsync(
                CreateRequest(image),
                TestContext.Current.CancellationToken);

            Assert.Equal("A red car turns left.", result.Answer);
            Assert.Equal(["v1/models", "v1/responses"], handler.Paths);
            Assert.Equal(["Bearer", "Bearer"], handler.AuthorizationSchemes);
            Assert.Contains("\"store\":false", handler.ResponseRequestBody, StringComparison.Ordinal);
            Assert.Contains("\"stream\":true", handler.ResponseRequestBody, StringComparison.Ordinal);
            Assert.Contains("input_image", handler.ResponseRequestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-oauth-token", handler.ResponseRequestBody, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\", handler.ResponseRequestBody, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task NoApprovedVisualEvidenceReturnsUncertainAnswerWithoutCallingProvider()
    {
        var handler = new PlanUsageHandler { FailIfCalled = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var provider = new ChatGptMomentVqaProvider(
            new FixedCredentialSource(),
            new ChatGptPlanResponsesClient(http),
            TimeProvider.System);
        var request = CreateRequest(null) with
        {
            SourceType = InteractiveVideoSourceType.YouTubeStream,
            SourceReference = "youtube:dQw4w9WgXcQ",
            Evidence = [],
            CaptureComplianceState = CaptureComplianceState.YoutubeCaptureDisabled,
        };

        var result = await provider.AskAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(0m, result.Confidence);
        Assert.Contains("visual evidence is unavailable", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Paths);
    }

    private static MomentVqaRequest CreateRequest(string? imagePath)
    {
        IReadOnlyList<MomentVqaEvidence> evidence = imagePath is null
            ? []
            :
            [
                new(
                    "frame-1",
                    MomentEvidenceKind.EphemeralImage,
                    MomentEvidenceBasis.NewLocalFrameAnalysis,
                    1000,
                    1000,
                    null,
                    imagePath,
                    640,
                    360,
                    1m,
                    true),
            ];
        return new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            InteractiveVideoSourceType.LocalFile,
            $"local:{Guid.NewGuid():N}",
            1000,
            "What is happening?",
            MomentQuestionIntent.Action,
            evidence,
            null,
            [],
            [],
            [],
            VqaProviderKind.ChatGpt,
            EvidenceRetentionPolicy.DeleteAfterRequest,
            CaptureComplianceState.AllowedLocalFile);
    }

    private sealed class FixedCredentialSource : IChatGptPlanCredentialSource
    {
        public VqaProviderAvailabilityState ConnectionState => VqaProviderAvailabilityState.PlanUsageAvailable;

        public Task<ChatGptPlanCredential?> GetSelectedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ChatGptPlanCredential?>(new(
                "secret-oauth-token",
                new HashSet<string>([ChatGptAuthorization.PlanUsageScope], StringComparer.Ordinal),
                DateTimeOffset.UtcNow.AddHours(1),
                "Connected account"));
    }

    private sealed class PlanUsageHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string?> AuthorizationSchemes { get; } = [];
        public string ResponseRequestBody { get; private set; } = string.Empty;
        public bool FailIfCalled { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (FailIfCalled)
            {
                throw new InvalidOperationException("Provider should not have been called.");
            }

            Paths.Add(request.RequestUri!.PathAndQuery.TrimStart('/'));
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
            if (request.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return Json("{\"models\":[{\"slug\":\"available-model\",\"display_name\":\"Available\",\"visibility\":\"list\"}]}");
            }

            ResponseRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.output_text.delta\",\"delta\":\"A red car \"}\n\n" +
                    "data: {\"type\":\"response.output_text.delta\",\"delta\":\"turns left.\"}\n\n" +
                    "data: {\"type\":\"response.completed\",\"response\":{}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
