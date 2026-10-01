namespace ADFi.Web;

public sealed record DiscoverSurface(
    string ProjectId,
    string Title,
    string YouTubeVideoId,
    string AccessibilitySummary,
    string Attribution,
    Uri CanonicalAdfiUrl,
    string? PublicationId = null);

public static class DiscoverSharingPolicy
{
    public static Uri CreateReferralUrl(Uri canonicalAdfiUrl, string signedReferralId)
    {
        ArgumentNullException.ThrowIfNull(canonicalAdfiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedReferralId);
        if (canonicalAdfiUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Public ADFi links must use HTTPS.", nameof(canonicalAdfiUrl));
        }

        var builder = new UriBuilder(canonicalAdfiUrl);
        var separator = string.IsNullOrEmpty(builder.Query) ? string.Empty : builder.Query.TrimStart('?') + "&";
        builder.Query = separator + "ref=" + Uri.EscapeDataString(signedReferralId);
        return builder.Uri;
    }
}
