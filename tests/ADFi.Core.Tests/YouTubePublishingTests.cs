using System.Net;
using System.Text;
using ADFi.Core;
using ADFi.YouTube;

namespace ADFi.Core.Tests;

public sealed class YouTubePublishingTests
{
    [Fact]
    public void InstalledAppAuthorizationUsesPkceStateAndOnlyRequiredYouTubeScopes()
    {
        var request = GoogleInstalledAppAuthorization.CreateRequest(
            new GoogleOAuthConfiguration("desktop-client", new Uri("http://127.0.0.1:48123/callback")));

        var query = request.AuthorizationUri.Query;
        Assert.Contains("code_challenge_method=S256", query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(GoogleInstalledAppAuthorization.UploadScope), query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(GoogleInstalledAppAuthorization.ChannelReadScope), query, StringComparison.Ordinal);
        Assert.DoesNotContain("force-ssl", query, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(request.State, request.CodeVerifier);
    }

    [Fact]
    public void CallbackWithWrongStateIsRejected()
    {
        var request = GoogleInstalledAppAuthorization.CreateRequest(
            new GoogleOAuthConfiguration("desktop-client", new Uri("http://localhost:48123/callback")));
        Assert.Throws<InvalidOperationException>(() => GoogleInstalledAppAuthorization.ValidateCallback(
            new Uri("http://localhost:48123/callback?code=abc&state=wrong"),
            request));
    }

    [Fact]
    public async Task PublisherConfirmsChannelThenUsesResumableVideosInsert()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adfi-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        try
        {
            var handler = new YouTubeHandler();
            using var client = new HttpClient(handler);
            var publisher = new GoogleYouTubePublisher(
                client,
                new FixedTokenProvider(),
                new FixedTimeProvider());

            var result = await publisher.PublishAsync(
                new YouTubePublicationRequest(
                    path,
                    "Test upload",
                    "Accessible description",
                    YouTubeVisibility.Unlisted,
                    new YouTubeChannelIdentity("channel-1", "Expected channel")),
                TestContext.Current.CancellationToken);

            Assert.Equal("video-1", result.VideoId);
            Assert.Equal("channel-1", result.ChannelId);
            Assert.Contains(handler.Requests, item => item.Contains("channels?", StringComparison.Ordinal));
            Assert.Contains(handler.Requests, item => item.Contains("videos?uploadType=resumable", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Requests, item => item.Contains("download", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PublisherRefusesDifferentAuthenticatedChannelBeforeUpload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adfi-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        try
        {
            var handler = new YouTubeHandler();
            using var client = new HttpClient(handler);
            var publisher = new GoogleYouTubePublisher(client, new FixedTokenProvider());

            await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
                new YouTubePublicationRequest(
                    path, "Test", string.Empty, YouTubeVisibility.Private,
                    new YouTubeChannelIdentity("other-channel", "Wrong channel")),
                TestContext.Current.CancellationToken));

            Assert.DoesNotContain(handler.Requests, item => item.Contains("videos?", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GoogleTokenServiceValidatesCallbackAndProtectsRefreshToken()
    {
        var handler = new GoogleTokenHandler();
        using var client = new HttpClient(handler);
        var secrets = new MemorySecretStore();
        var configuration = new GoogleOAuthConfiguration(
            "desktop-client",
            new Uri("http://127.0.0.1:48123/callback"));
        var authorization = GoogleInstalledAppAuthorization.CreateRequest(configuration);
        var service = new GoogleTokenService(client, configuration, secrets, "youtube:user-1", new FixedTimeProvider());
        var callback = new Uri(
            $"http://127.0.0.1:48123/callback?code=authorization-code&state={Uri.EscapeDataString(authorization.State)}");

        var token = await service.ExchangeAuthorizationCodeAsync(
            callback,
            authorization,
            TestContext.Current.CancellationToken);

        Assert.Equal("access-1", token.AccessToken);
        Assert.Equal("refresh-1", token.RefreshToken);
        Assert.NotNull(await secrets.LoadAsync("youtube:user-1", TestContext.Current.CancellationToken));
        Assert.Contains("code_verifier", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusRefreshReadsProviderStateAndDoesNotInventRightsClaims()
    {
        var handler = new YouTubeHandler();
        using var client = new HttpClient(handler);
        var publisher = new GoogleYouTubePublisher(client, new FixedTokenProvider(), new FixedTimeProvider());

        var status = await publisher.RefreshAsync("abcDEF123_-", TestContext.Current.CancellationToken);
        var decision = YouTubeStatusPolicy.Evaluate(status, ProjectPublicationState.CommunityActive);

        Assert.True(status.Exists);
        Assert.Equal("channel-1", status.ChannelId);
        Assert.Equal(YouTubeVisibility.Public, status.Visibility);
        Assert.Equal(ProjectPublicationState.CommunityActive, decision.ProjectState);
        Assert.True(decision.Availability.Playable);
    }

    [Theory]
    [InlineData(false, "processed", true, YouTubeVisibility.Public, ProjectPublicationState.Removed)]
    [InlineData(true, "rejected", true, YouTubeVisibility.Public, ProjectPublicationState.Blocked)]
    [InlineData(true, "processed", false, YouTubeVisibility.Public, ProjectPublicationState.Blocked)]
    [InlineData(true, "processed", true, YouTubeVisibility.Private, ProjectPublicationState.Private)]
    [InlineData(true, "processed", true, YouTubeVisibility.Unlisted, ProjectPublicationState.Unlisted)]
    public void StatusPolicyFailsClosedForUnavailableAssets(
        bool exists,
        string uploadStatus,
        bool embeddable,
        YouTubeVisibility visibility,
        ProjectPublicationState expected)
    {
        var status = new YouTubeAssetStatus(
            "abcDEF123_-", exists, "channel-1", uploadStatus, visibility, embeddable, null, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, YouTubeStatusPolicy.Evaluate(status, ProjectPublicationState.CommunityActive).ProjectState);
    }

    private sealed class FixedTokenProvider : IYouTubeAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("test-access-token");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class YouTubeHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath.EndsWith("/channels", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, "{\"items\":[{\"id\":\"channel-1\",\"snippet\":{\"title\":\"Expected channel\"}}]}");
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/videos", StringComparison.Ordinal)
                && request.Method == HttpMethod.Get)
            {
                return Json(
                    HttpStatusCode.OK,
                    "{\"items\":[{\"id\":\"abcDEF123_-\",\"snippet\":{\"channelId\":\"channel-1\"},\"status\":{\"uploadStatus\":\"processed\",\"privacyStatus\":\"public\",\"embeddable\":true}}]}");
            }

            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri("https://upload.youtube.test/session/1");
                return Task.FromResult(response);
            }

            return Json(HttpStatusCode.OK, "{\"id\":\"video-1\"}");
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, string json) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class GoogleTokenHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var scopes = $"{GoogleInstalledAppAuthorization.UploadScope} {GoogleInstalledAppAuthorization.ChannelReadScope}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600,\"scope\":\"{scopes}\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class MemorySecretStore : IProtectedSecretStore
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);

        public Task SaveAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
        {
            values[key] = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? value.ToArray() : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
