using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ADFi.YouTube;

/// <summary>
/// Upload-only YouTube Data API client. ADFi never accepts a YouTube URL as source media and
/// intentionally contains no video acquisition/download operation.
/// </summary>
public sealed class GoogleYouTubePublisher(
    HttpClient httpClient,
    IYouTubeAccessTokenProvider accessTokenProvider,
    TimeProvider? timeProvider = null) : IYouTubePublisher, IYouTubeStatusClient
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<YouTubeChannelIdentity> GetAuthenticatedChannelAsync(
        CancellationToken cancellationToken)
    {
        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = AuthorizedRequest(
            HttpMethod.Get,
            "https://www.googleapis.com/youtube/v3/channels?part=id,snippet&mine=true&maxResults=1",
            token);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "YOUTUBE_CHANNEL_LOOKUP_FAILED", cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ChannelListResponse>(cancellationToken)
            .ConfigureAwait(false);
        var channel = result?.Items?.SingleOrDefault()
                      ?? throw new InvalidOperationException("The authenticated Google account has no usable YouTube channel.");
        return new(channel.Id, channel.Snippet.Title);
    }

    public async Task<YouTubePublicationResult> PublishAsync(
        YouTubePublicationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = Path.GetFullPath(request.LocalFinalMediaPath);
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Only an existing finalized local MP4 can be published.", path);
        }

        if (request.Title.Length is < 1 or > 100 || request.Description.Length > 5_000)
        {
            throw new ArgumentException("YouTube title or description length is invalid.", nameof(request));
        }

        var actualChannel = await GetAuthenticatedChannelAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualChannel.ChannelId, request.ConfirmedChannel.ChannelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication stopped because the authenticated channel is '{actualChannel.DisplayName}', not the channel the user confirmed.");
        }

        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var file = new FileInfo(path);
        using var initialize = AuthorizedRequest(
            HttpMethod.Post,
            "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status",
            token);
        initialize.Headers.Add("X-Upload-Content-Length", file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        initialize.Headers.Add("X-Upload-Content-Type", "video/mp4");
        initialize.Content = JsonContent.Create(new
        {
            snippet = new { title = request.Title, description = request.Description },
            status = new { privacyStatus = ToApiVisibility(request.Visibility), selfDeclaredMadeForKids = false },
        });
        using var initialized = await httpClient.SendAsync(initialize, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(initialized, "YOUTUBE_UPLOAD_INITIALIZATION_FAILED", cancellationToken)
            .ConfigureAwait(false);
        var uploadUri = initialized.Headers.Location
                        ?? throw new InvalidOperationException("YouTube returned no resumable upload location.");

        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var upload = new HttpRequestMessage(HttpMethod.Put, uploadUri)
        {
            Content = new StreamContent(input, 1024 * 1024),
        };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        upload.Content.Headers.ContentLength = file.Length;
        using var uploaded = await httpClient.SendAsync(
            upload,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(uploaded, "YOUTUBE_UPLOAD_FAILED", cancellationToken).ConfigureAwait(false);
        var result = await uploaded.Content.ReadFromJsonAsync<VideoInsertResponse>(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result?.Id))
        {
            throw new InvalidOperationException("YouTube completed the upload without returning a video ID.");
        }

        return new(result.Id, actualChannel.ChannelId, request.Visibility, clock.GetUtcNow());
    }

    public async Task<YouTubeAssetStatus> RefreshAsync(
        string videoId,
        CancellationToken cancellationToken)
    {
        if (!IsValidVideoId(videoId))
        {
            throw new ArgumentException("YouTube video ID is invalid.", nameof(videoId));
        }

        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = AuthorizedRequest(
            HttpMethod.Get,
            $"https://www.googleapis.com/youtube/v3/videos?part=id,snippet,status&id={Uri.EscapeDataString(videoId)}",
            token);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "YOUTUBE_STATUS_REFRESH_FAILED", cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<VideoListResponse>(cancellationToken)
            .ConfigureAwait(false);
        var video = result?.Items?.SingleOrDefault();
        if (video is null)
        {
            return new(videoId, false, null, null, null, false, "NOT_RETURNED_BY_YOUTUBE", clock.GetUtcNow());
        }

        return new(
            video.Id,
            true,
            video.Snippet.ChannelId,
            video.Status.UploadStatus,
            ParseVisibility(video.Status.PrivacyStatus),
            video.Status.Embeddable,
            video.Status.FailureReason ?? video.Status.RejectionReason,
            clock.GetUtcNow());
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string uri, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var compact = string.Join(' ', body.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        throw new InvalidOperationException(
            $"{errorCode}: HTTP {(int)response.StatusCode}. {compact[..Math.Min(compact.Length, 500)]}");
    }

    private static string ToApiVisibility(YouTubeVisibility visibility) => visibility switch
    {
        YouTubeVisibility.Private => "private",
        YouTubeVisibility.Unlisted => "unlisted",
        YouTubeVisibility.Public => "public",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility)),
    };

    private static YouTubeVisibility? ParseVisibility(string? value) => value switch
    {
        "private" => YouTubeVisibility.Private,
        "unlisted" => YouTubeVisibility.Unlisted,
        "public" => YouTubeVisibility.Public,
        _ => null,
    };

    private static bool IsValidVideoId(string value) =>
        value.Length == 11 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private sealed record ChannelListResponse(
        [property: JsonPropertyName("items")] IReadOnlyList<ChannelItem>? Items);

    private sealed record ChannelItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("snippet")] ChannelSnippet Snippet);

    private sealed record ChannelSnippet([property: JsonPropertyName("title")] string Title);

    private sealed record VideoInsertResponse([property: JsonPropertyName("id")] string Id);

    private sealed record VideoListResponse(
        [property: JsonPropertyName("items")] IReadOnlyList<VideoItem>? Items);

    private sealed record VideoItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("snippet")] VideoSnippet Snippet,
        [property: JsonPropertyName("status")] VideoStatus Status);

    private sealed record VideoSnippet([property: JsonPropertyName("channelId")] string ChannelId);

    private sealed record VideoStatus(
        [property: JsonPropertyName("uploadStatus")] string? UploadStatus,
        [property: JsonPropertyName("privacyStatus")] string? PrivacyStatus,
        [property: JsonPropertyName("embeddable")] bool Embeddable,
        [property: JsonPropertyName("failureReason")] string? FailureReason,
        [property: JsonPropertyName("rejectionReason")] string? RejectionReason);
}
