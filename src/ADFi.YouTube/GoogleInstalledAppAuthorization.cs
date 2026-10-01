using System.Security.Cryptography;
using System.Text;

namespace ADFi.YouTube;

public static class GoogleInstalledAppAuthorization
{
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";
    public const string ChannelReadScope = "https://www.googleapis.com/auth/youtube.readonly";

    public static GoogleAuthorizationRequest CreateRequest(GoogleOAuthConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ClientId);
        ValidateLoopback(configuration.LoopbackRedirectUri);

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = configuration.ClientId,
            ["redirect_uri"] = configuration.LoopbackRedirectUri.AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = $"{UploadScope} {ChannelReadScope}",
            ["access_type"] = "offline",
            ["include_granted_scopes"] = "true",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        var uri = new Uri(AuthorizationEndpoint + "?" + string.Join(
            '&',
            query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
        return new(uri, state, verifier, configuration.LoopbackRedirectUri);
    }

    public static void ValidateCallback(Uri callback, GoogleAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(request);
        var values = ParseQuery(callback.Query);
        if (!values.TryGetValue("state", out var state)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.State),
                Encoding.UTF8.GetBytes(state)))
        {
            throw new InvalidOperationException("Google authorization state validation failed.");
        }

        if (values.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException($"Google authorization was not completed: {error}");
        }

        if (!values.ContainsKey("code"))
        {
            throw new InvalidOperationException("Google authorization returned no code.");
        }
    }

    public static string GetAuthorizationCode(Uri callback)
    {
        var values = ParseQuery(callback.Query);
        return values.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code)
            ? code
            : throw new InvalidOperationException("Google authorization returned no code.");
    }

    private static void ValidateLoopback(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp
            || (uri.Host != "127.0.0.1" && uri.Host != "localhost" && uri.Host != "[::1]"))
        {
            throw new ArgumentException("Google desktop authorization must use an HTTP loopback redirect.", nameof(uri));
        }
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty,
                StringComparer.Ordinal);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
