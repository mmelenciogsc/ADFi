using System.Net;
using System.Text;
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
}
