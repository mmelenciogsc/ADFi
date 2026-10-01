using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADFi.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ADFi.Infrastructure.CloudAI;

public sealed record ChatGptProfileSummary(
    string ProfileId,
    string DisplayName,
    string? Email,
    IReadOnlySet<string> GrantedScopes,
    DateTimeOffset ExpiresAtUtc,
    bool IsSelected);

public sealed record ChatGptConnectionResult(
    ChatGptProfileSummary Profile,
    VqaProviderAvailabilityState State,
    string AccessibleMessage);

/// <summary>
/// Owns the complete local Sign in with ChatGPT profile lifecycle. The encrypted vault is the
/// only persisted representation of tokens; browser URLs, callbacks, tokens and local identity
/// claims are deliberately never logged.
/// </summary>
public sealed class ChatGptProfileService : IChatGptPlanCredentialSource, IAsyncDisposable
{
    private const string VaultKey = "chatgpt-plan-profiles-v1";
    private static readonly Uri Issuer = new("https://auth.openai.com");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IProtectedSecretStore secretStore;
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly bool integrationEnabled;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ChatGptProfileVault? vault;
    private bool disposed;

    public ChatGptProfileService(
        IProtectedSecretStore secretStore,
        HttpClient httpClient,
        TimeProvider timeProvider,
        bool integrationEnabled)
    {
        this.secretStore = secretStore;
        this.httpClient = httpClient;
        this.timeProvider = timeProvider;
        this.integrationEnabled = integrationEnabled;
        ConnectionState = integrationEnabled
            ? VqaProviderAvailabilityState.NotConnected
            : VqaProviderAvailabilityState.ProviderApprovalRequired;
    }

    public VqaProviderAvailabilityState ConnectionState { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (vault is not null)
            {
                return;
            }

            var protectedValue = await secretStore.LoadAsync(VaultKey, cancellationToken).ConfigureAwait(false);
            if (protectedValue is null)
            {
                vault = new(Guid.NewGuid(), null, []);
                await SaveLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    vault = JsonSerializer.Deserialize<ChatGptProfileVault>(protectedValue, JsonOptions)
                            ?? throw new InvalidDataException("The protected ChatGPT profile vault is empty.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedValue);
                }
            }

            UpdateConnectionStateLocked();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ChatGptProfileSummary>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return vault!.Profiles.Select(ToSummaryLocked).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        EnsureEnabled();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!vault!.Profiles.Any(item => string.Equals(item.ProfileId, profileId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("That ChatGPT profile is no longer available.");
            }

            vault = vault with { SelectedProfileId = profileId };
            await SaveLockedAsync(cancellationToken).ConfigureAwait(false);
            UpdateConnectionStateLocked();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ChatGptPlanCredential?> GetSelectedAsync(CancellationToken cancellationToken)
    {
        if (!integrationEnabled)
        {
            ConnectionState = VqaProviderAvailabilityState.ProviderApprovalRequired;
            return null;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = SelectedProfileLocked();
            if (profile is null)
            {
                ConnectionState = VqaProviderAvailabilityState.NotConnected;
                return null;
            }

            if (profile.ExpiresAtUtc <= timeProvider.GetUtcNow().AddMinutes(2))
            {
                profile = await RefreshLockedAsync(profile, cancellationToken).ConfigureAwait(false);
            }

            ConnectionState = ChatGptAuthorization.GrantsPlanUsage(profile.GrantedScopes)
                ? VqaProviderAvailabilityState.PlanUsageAvailable
                : VqaProviderAvailabilityState.ConnectedWithoutPlanUsage;
            return new(
                profile.AccessToken,
                profile.GrantedScopes,
                profile.ExpiresAtUtc,
                profile.DisplayName);
        }
        catch (ChatGptPlanUsageException exception)
        {
            ConnectionState = exception.State;
            return null;
        }
        catch (HttpRequestException)
        {
            ConnectionState = VqaProviderAvailabilityState.Unavailable;
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ChatGptConnectionResult> SignInAsync(
        Func<Uri, Task> openSystemBrowser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openSystemBrowser);
        EnsureEnabled();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        ChatGptAuthorizationAttempt attempt;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selected = SelectedProfileLocked();
            attempt = ChatGptAuthorization.CreateAttempt(
                vault!.HostId,
                port,
                timeProvider,
                selected?.ClientId) with
            {
                IdTokenHint = selected?.IdToken,
                LoginHint = selected?.Email,
            };
        }
        finally
        {
            gate.Release();
        }

        await openSystemBrowser(ChatGptAuthorization.BuildAuthorizationUri(attempt)).ConfigureAwait(false);
        var callback = await ReceiveCallbackAsync(listener, cancellationToken).ConfigureAwait(false);
        ChatGptAuthorization.ValidateCallback(attempt, callback);
        var clientId = callback.IssuedClientId ?? attempt.ExpectedIssuedClientId
            ?? throw new InvalidOperationException("ChatGPT did not return a registered client ID.");
        var token = await ExchangeCodeAsync(attempt, callback.Code, clientId, cancellationToken)
            .ConfigureAwait(false);
        var identity = await ValidateIdentityAsync(token.IdToken, clientId, attempt.Nonce, cancellationToken)
            .ConfigureAwait(false);
        var scopes = ParseScopes(token.Scope, callback.GrantedScopes);
        var accessToken = Require(token.AccessToken, "access token");
        var profile = new StoredChatGptProfile(
            identity.Subject,
            identity.DisplayName,
            identity.Email,
            clientId,
            accessToken,
            token.RefreshToken,
            token.IdToken,
            scopes,
            timeProvider.GetUtcNow().AddSeconds(Math.Max(1, token.ExpiresIn)));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = vault!.Profiles
                .Where(item => !string.Equals(item.ProfileId, profile.ProfileId, StringComparison.Ordinal))
                .Append(profile)
                .ToArray();
            vault = vault with { SelectedProfileId = profile.ProfileId, Profiles = profiles };
            await SaveLockedAsync(cancellationToken).ConfigureAwait(false);
            UpdateConnectionStateLocked();
            var summary = ToSummaryLocked(profile);
            return new(
                summary,
                ConnectionState,
                ConnectionState == VqaProviderAvailabilityState.PlanUsageAvailable
                    ? $"Connected as {profile.DisplayName}. ChatGPT plan usage available."
                    : $"Connected as {profile.DisplayName}, but plan usage was not granted.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SignOutSelectedAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = SelectedProfileLocked();
            if (profile is null)
            {
                return;
            }

            if (integrationEnabled && !string.IsNullOrWhiteSpace(profile.RefreshToken))
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = profile.ClientId,
                    ["token"] = profile.RefreshToken,
                    ["token_type_hint"] = "refresh_token",
                });
                try
                {
                    using var response = await httpClient.PostAsync(
                        ChatGptAuthorization.RevocationEndpoint,
                        content,
                        cancellationToken).ConfigureAwait(false);
                    _ = response.StatusCode;
                }
                catch (HttpRequestException)
                {
                    // Local sign-out must still revoke local access when the provider is unreachable.
                }
            }

            var remaining = vault!.Profiles
                .Where(item => !string.Equals(item.ProfileId, profile.ProfileId, StringComparison.Ordinal))
                .ToArray();
            vault = vault with
            {
                Profiles = remaining,
                SelectedProfileId = remaining.FirstOrDefault()?.ProfileId,
            };
            await SaveLockedAsync(cancellationToken).ConfigureAwait(false);
            UpdateConnectionStateLocked();
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (vault is null)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StoredChatGptProfile> RefreshLockedAsync(
        StoredChatGptProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            throw new ChatGptPlanUsageException(
                "The ChatGPT connection must be authorized again.",
                "missing_refresh_token",
                VqaProviderAvailabilityState.ReauthorizationRequired);
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = profile.ClientId,
            ["refresh_token"] = profile.RefreshToken,
            ["resource"] = ChatGptAuthorization.ApiResource,
        });
        using var response = await httpClient.PostAsync(
            ChatGptAuthorization.TokenEndpoint,
            content,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ChatGptPlanUsageException(
                "The ChatGPT connection must be authorized again.",
                "refresh_failed",
                VqaProviderAvailabilityState.ReauthorizationRequired,
                response.StatusCode);
        }

        var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("ChatGPT returned an empty refresh response.");
        var refreshed = profile with
        {
            AccessToken = Require(token.AccessToken, "access token"),
            RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? profile.RefreshToken : token.RefreshToken,
            IdToken = string.IsNullOrWhiteSpace(token.IdToken) ? profile.IdToken : token.IdToken,
            GrantedScopes = ParseScopes(token.Scope, profile.GrantedScopes),
            ExpiresAtUtc = timeProvider.GetUtcNow().AddSeconds(Math.Max(1, token.ExpiresIn)),
        };
        vault = vault! with
        {
            Profiles = vault.Profiles.Select(item => item.ProfileId == profile.ProfileId ? refreshed : item).ToArray(),
        };
        await SaveLockedAsync(cancellationToken).ConfigureAwait(false);
        return refreshed;
    }

    private async Task<OAuthTokenResponse> ExchangeCodeAsync(
        ChatGptAuthorizationAttempt attempt,
        string code,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = attempt.RedirectUri.AbsoluteUri,
            ["code_verifier"] = attempt.PkceVerifier,
            ["resource"] = ChatGptAuthorization.ApiResource,
        });
        using var response = await httpClient.PostAsync(
            ChatGptAuthorization.TokenEndpoint,
            content,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("ChatGPT returned an empty token response.");
        _ = Require(token.AccessToken, "access token");
        _ = Require(token.RefreshToken, "refresh token");
        _ = Require(token.IdToken, "identity token");
        return token;
    }

    private async Task<ValidatedIdentity> ValidateIdentityAsync(
        string? idToken,
        string clientId,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        var encodedToken = Require(idToken, "identity token");
        var jwksJson = await httpClient.GetStringAsync(ChatGptAuthorization.JwksEndpoint, cancellationToken)
            .ConfigureAwait(false);
        var signingKeys = new JsonWebKeySet(jwksJson).GetSigningKeys();
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(encodedToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer.AbsoluteUri.TrimEnd('/'),
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ClockSkew = TimeSpan.FromMinutes(2),
        }).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new SecurityTokenValidationException("ChatGPT identity validation failed.", result.Exception);
        }

        var claims = result.ClaimsIdentity.Claims.ToArray();
        var nonce = Claim(claims, "nonce");
        if (!FixedTimeEquals(expectedNonce, nonce))
        {
            throw new SecurityTokenValidationException("ChatGPT identity nonce validation failed.");
        }

        var subject = Require(Claim(claims, "sub"), "subject claim");
        var email = Claim(claims, "email");
        var name = Claim(claims, "name");
        return new(subject, string.IsNullOrWhiteSpace(name) ? email ?? "ChatGPT account" : name, email);
    }

    private static async Task<ChatGptAuthorizationCallback> ReceiveCallbackAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("ChatGPT returned an empty browser callback.");
        var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2 || requestParts[1].Length > 8192)
        {
            throw new InvalidDataException("ChatGPT returned an invalid browser callback.");
        }

        string? header;
        var totalHeaderLength = requestLine.Length;
        do
        {
            header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            totalHeaderLength += header?.Length ?? 0;
            if (totalHeaderLength > 32_768)
            {
                throw new InvalidDataException("ChatGPT returned an oversized browser callback.");
            }
        } while (!string.IsNullOrEmpty(header));

        var callbackUri = new Uri("http://127.0.0.1" + requestParts[1]);
        if (!string.Equals(callbackUri.AbsolutePath, "/auth/callback", StringComparison.Ordinal))
        {
            throw new InvalidDataException("ChatGPT returned to an unexpected callback path.");
        }

        var query = ParseQuery(callbackUri.Query);
        var body = query.TryGetValue("error", out var error)
            ? "ADFi could not connect ChatGPT. You may close this tab and return to ADFi."
            : "ChatGPT is connected to ADFi. You may close this tab and return to ADFi.";
        var status = query.ContainsKey("error") ? "400 Bad Request" : "200 OK";
        var html = $"<!doctype html><html><body><h1>{WebUtility.HtmlEncode(body)}</h1></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

        if (query.TryGetValue("error", out error))
        {
            throw new InvalidOperationException($"ChatGPT authorization was not completed: {SanitizeOAuthError(error)}.");
        }

        return new(
            Value(query, "code"),
            Value(query, "state"),
            query.GetValueOrDefault("client_id"),
            ParseScopes(query.GetValueOrDefault("scope"), []));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] =
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return result;
    }

    private static HashSet<string> ParseScopes(string? scope, IEnumerable<string> fallback)
    {
        var values = string.IsNullOrWhiteSpace(scope)
            ? fallback
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return values.ToHashSet(StringComparer.Ordinal);
    }

    private static string Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"ChatGPT callback did not include {key}.");

    private static string? Claim(IEnumerable<Claim> claims, string type) =>
        claims.FirstOrDefault(item => string.Equals(item.Type, type, StringComparison.Ordinal))?.Value;

    private static string Require(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"ChatGPT did not return a required {name}.");

    private static bool FixedTimeEquals(string left, string? right)
    {
        if (right is null)
        {
            return false;
        }

        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string SanitizeOAuthError(string value) => value switch
    {
        "access_denied" => "access was denied",
        "invalid_request" => "the authorization request was rejected",
        _ => "the provider returned an authorization error",
    };

    private StoredChatGptProfile? SelectedProfileLocked() =>
        vault!.Profiles.FirstOrDefault(item =>
            string.Equals(item.ProfileId, vault.SelectedProfileId, StringComparison.Ordinal));

    private ChatGptProfileSummary ToSummaryLocked(StoredChatGptProfile profile) => new(
        profile.ProfileId,
        profile.DisplayName,
        profile.Email,
        profile.GrantedScopes,
        profile.ExpiresAtUtc,
        string.Equals(profile.ProfileId, vault!.SelectedProfileId, StringComparison.Ordinal));

    private void UpdateConnectionStateLocked()
    {
        if (!integrationEnabled)
        {
            ConnectionState = VqaProviderAvailabilityState.ProviderApprovalRequired;
            return;
        }

        var profile = SelectedProfileLocked();
        ConnectionState = profile is null
            ? VqaProviderAvailabilityState.NotConnected
            : !ChatGptAuthorization.GrantsPlanUsage(profile.GrantedScopes)
                ? VqaProviderAvailabilityState.ConnectedWithoutPlanUsage
                : profile.ExpiresAtUtc <= timeProvider.GetUtcNow() && string.IsNullOrWhiteSpace(profile.RefreshToken)
                    ? VqaProviderAvailabilityState.ReauthorizationRequired
                    : VqaProviderAvailabilityState.PlanUsageAvailable;
    }

    private async Task SaveLockedAsync(CancellationToken cancellationToken)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(vault, JsonOptions);
        try
        {
            await secretStore.SaveAsync(VaultKey, serialized, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private void EnsureEnabled()
    {
        if (!integrationEnabled)
        {
            throw new InvalidOperationException(
                "ChatGPT plan usage is disabled until OpenAI approves ADFi's paid application integration.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed record ChatGptProfileVault(
        Guid HostId,
        string? SelectedProfileId,
        IReadOnlyList<StoredChatGptProfile> Profiles);

    private sealed record StoredChatGptProfile(
        string ProfileId,
        string DisplayName,
        string? Email,
        string ClientId,
        string AccessToken,
        string? RefreshToken,
        string? IdToken,
        HashSet<string> GrantedScopes,
        DateTimeOffset ExpiresAtUtc);

    private sealed record ValidatedIdentity(string Subject, string DisplayName, string? Email);

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
