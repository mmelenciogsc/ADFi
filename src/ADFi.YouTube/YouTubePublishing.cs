namespace ADFi.YouTube;

public enum YouTubeVisibility
{
    Private,
    Unlisted,
    Public,
}

public sealed record YouTubeChannelIdentity(string ChannelId, string DisplayName);

public sealed record YouTubePublicationRequest(
    string LocalFinalMediaPath,
    string Title,
    string Description,
    YouTubeVisibility Visibility,
    YouTubeChannelIdentity ConfirmedChannel);

public sealed record YouTubePublicationResult(
    string VideoId,
    string ChannelId,
    YouTubeVisibility Visibility,
    DateTimeOffset PublishedAtUtc);

public interface IYouTubePublisher
{
    Task<YouTubeChannelIdentity> GetAuthenticatedChannelAsync(CancellationToken cancellationToken);

    Task<YouTubePublicationResult> PublishAsync(
        YouTubePublicationRequest request,
        CancellationToken cancellationToken);
}
