namespace ADFi.Web;

public sealed record DiscoverSurface(
    string ProjectId,
    string Title,
    string YouTubeVideoId,
    string AccessibilitySummary,
    string Attribution,
    Uri CanonicalAdfiUrl);
