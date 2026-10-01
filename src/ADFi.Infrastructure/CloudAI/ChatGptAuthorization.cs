using System.Security.Cryptography;
using System.Text;

namespace ADFi.Infrastructure.CloudAI;

public sealed record ChatGptAuthorizationAttempt(
    string HostId,
    string State,
    string Nonce,
    string PkceVerifier,
    string PkceChallenge,
    Uri RedirectUri,
    string RequestedClientId,
    string? ExpectedIssuedClientId,
    DateTimeOffset CreatedAtUtc);

public sealed record ChatGptAuthorizationCallback(
    string Code,
    string State,
    string? IssuedClientId,
    IReadOnlySet<string> GrantedScopes);

public static class ChatGptAuthorization
{
    public const string InitialClientId = "dynamic_agent_client";
    public const string PlanUsageScope = "chatgpt.tokens.use.direct";
    public const string ApiResource = "https://api.openai.com/v1";
    public static readonly Uri AuthorizationEndpoint = new("https://auth.openai.com/api/accounts/authorize");
    public static readonly Uri TokenEndpoint = new("https://auth.openai.com/api/accounts/oauth/token");

    private const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    public static ChatGptAuthorizationAttempt CreateAttempt(
        Guid persistentHostId,
        int loopbackPort,
        TimeProvider timeProvider,
        string? issuedClientId = null)
    {
        if (loopbackPort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(loopbackPort));
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        return new(
            $"urn:uuid:{persistentHostId:D}",
            Base64Url(RandomNumberGenerator.GetBytes(32)),
            Base64Url(RandomNumberGenerator.GetBytes(32)),
            verifier,
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            new($"http://127.0.0.1:{loopbackPort}/auth/callback"),
            issuedClientId ?? InitialClientId,
            issuedClientId,
            timeProvider.GetUtcNow());
    }

    public static Uri BuildAuthorizationUri(ChatGptAuthorizationAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = attempt.RequestedClientId,
            ["ext_agent_host_id"] = attempt.HostId,
            ["response_type"] = "code",
            ["redirect_uri"] = attempt.RedirectUri.AbsoluteUri,
            ["scope"] = Scope,
            ["resource"] = ApiResource,
            ["state"] = attempt.State,
            ["nonce"] = attempt.Nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = attempt.PkceChallenge,
        };
        if (attempt.RequestedClientId == InitialClientId)
        {
            values["agent_name_hint"] = "ADFi";
        }

        var query = string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(AuthorizationEndpoint) { Query = query }.Uri;
    }

    public static void ValidateCallback(
        ChatGptAuthorizationAttempt attempt,
        ChatGptAuthorizationCallback callback)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(callback);
        if (!FixedTimeEquals(attempt.State, callback.State))
        {
            throw new InvalidOperationException("ChatGPT authorization state mismatch.");
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            throw new InvalidOperationException("ChatGPT authorization did not return a code.");
        }

        if (attempt.RequestedClientId == InitialClientId && string.IsNullOrWhiteSpace(callback.IssuedClientId))
        {
            throw new InvalidOperationException("ChatGPT dynamic registration did not return an issued client ID.");
        }

        if (attempt.ExpectedIssuedClientId is not null
            && callback.IssuedClientId is not null
            && !string.Equals(attempt.ExpectedIssuedClientId, callback.IssuedClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ChatGPT authorization returned a different registered client ID.");
        }
    }

    public static bool GrantsPlanUsage(IReadOnlySet<string> grantedScopes) =>
        grantedScopes.Contains(PlanUsageScope);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}

