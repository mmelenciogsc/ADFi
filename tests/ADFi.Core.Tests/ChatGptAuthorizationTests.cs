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
    public void ReturningAuthorizationUsesRegisteredClientAndAccountHints()
    {
        var attempt = ChatGptAuthorization.CreateAttempt(
            Guid.NewGuid(),
            1455,
            TimeProvider.System,
            "oaiapp_registered") with
        {
            IdTokenHint = "signed.identity.token",
            LoginHint = "listener@example.test",
        };

        var uri = ChatGptAuthorization.BuildAuthorizationUri(attempt);

        Assert.Contains("client_id=oaiapp_registered", uri.Query, StringComparison.Ordinal);
        Assert.Contains("id_token_hint=signed.identity.token", uri.Query, StringComparison.Ordinal);
        Assert.Contains("login_hint=listener%40example.test", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_name_hint", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderApprovalGatePreventsCredentialUse()
    {
        var store = new MemorySecretStore();
        using var http = new HttpClient(new FailIfCalledHandler());
        await using var service = new ChatGptProfileService(store, http, TimeProvider.System, false);

        await service.InitializeAsync(TestContext.Current.CancellationToken);
        var credential = await service.GetSelectedAsync(TestContext.Current.CancellationToken);

        Assert.Null(credential);
        Assert.Equal(VqaProviderAvailabilityState.ProviderApprovalRequired, service.ConnectionState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignInAsync(
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredProfileRefreshesAndPersistsRotatedCredentials()
    {
        var store = new MemorySecretStore();
        var initialVault = $$"""
            {
              "hostId": "{{Guid.NewGuid()}}",
              "selectedProfileId": "subject-1",
              "profiles": [{
                "profileId": "subject-1",
                "displayName": "Accessible Tester",
                "email": "tester@example.test",
                "clientId": "oaiapp_registered",
                "accessToken": "expired-access",
                "refreshToken": "old-refresh",
                "idToken": "old-id-token",
                "grantedScopes": ["{{ChatGptAuthorization.PlanUsageScope}}"],
                "expiresAtUtc": "2000-01-01T00:00:00+00:00"
              }]
            }
            """;
        store.Value = Encoding.UTF8.GetBytes(initialVault);
        var handler = new RefreshHandler();
        using var http = new HttpClient(handler);
        await using (var service = new ChatGptProfileService(store, http, TimeProvider.System, true))
        {
            var credential = await service.GetSelectedAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(credential);
            Assert.Equal("rotated-access", credential.AccessToken);
            Assert.Equal(VqaProviderAvailabilityState.PlanUsageAvailable, service.ConnectionState);
        }

        var persisted = Encoding.UTF8.GetString(store.Value!);
        Assert.Contains("rotated-access", persisted, StringComparison.Ordinal);
        Assert.Contains("rotated-refresh", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("expired-access", persisted, StringComparison.Ordinal);
        Assert.Contains("grant_type=refresh_token", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("resource=https%3A%2F%2Fapi.openai.com%2Fv1", handler.RequestBody, StringComparison.Ordinal);
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

    private sealed class MemorySecretStore : IProtectedSecretStore
    {
        public byte[]? Value { get; set; }

        public Task SaveAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
        {
            Value = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Value?.ToArray());

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Value = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FailIfCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP must not be called while the integration gate is disabled.");
    }

    private sealed class RefreshHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ChatGptAuthorization.TokenEndpoint, request.RequestUri);
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"rotated-access\",\"refresh_token\":\"rotated-refresh\",\"scope\":\"chatgpt.tokens.use.direct\",\"expires_in\":3600}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
