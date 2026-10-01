using System.IO;

namespace ADFi.Desktop;

public sealed record DesktopConfiguration(
    Uri ApiBaseUri,
    string ProjectRoot,
    string ProtectedCredentialRoot,
    string HardwareProfileRoot,
    string ViewerEvidenceRoot)
{
    public static DesktopConfiguration Load()
    {
        var configuredApi = Environment.GetEnvironmentVariable("ADFI_API_BASE_URL");
        var apiBaseUri = Uri.TryCreate(configuredApi, UriKind.Absolute, out var parsed)
            ? parsed
            : new Uri("https://BLACKOUT/");
        if (apiBaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("ADFI_API_BASE_URL must use HTTPS.");
        }

        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
        {
            throw new InvalidOperationException("The Windows local application-data directory is unavailable.");
        }

        var root = Path.Combine(applicationData, "ADFi");
        return new(
            EnsureTrailingSlash(apiBaseUri),
            Path.Combine(root, "Projects"),
            Path.Combine(root, "Secure"),
            Path.Combine(root, "Hardware"),
            Path.Combine(root, "PrivateViewerEvidence"));
    }

    private static Uri EnsureTrailingSlash(Uri value) =>
        value.AbsoluteUri.EndsWith('/')
            ? value
            : new Uri(value.AbsoluteUri + "/");
}
