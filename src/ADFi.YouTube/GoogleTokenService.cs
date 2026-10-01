using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADFi.Core;

namespace ADFi.YouTube;

public sealed class GoogleTokenService(
    HttpClient httpClient,
    GoogleOAuthConfiguration configuration,
    IProtectedSecretStore secretStore,
    string accountStorageKey,
    TimeProvider? timeProvider = null) : IYouTubeAccessTokenProvider, IDisposable
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private GoogleTokenSet? current;

    public async Task<GoogleTokenSet> ExchangeAuthorizationCodeAsync(
        Uri callback,
        GoogleAuthorizationRequest authorizationRequest,
        CancellationToken cancellationToken)
    {
        GoogleInstalledAppAuthorization.ValidateCallback(callback, authorizationRequest);
        using var response = await httpClient.PostAsync(
            GoogleInstalledAppAuthorization.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = configuration.ClientId,
                ["code"] = GoogleInstalledAppAuthorization.GetAuthorizationCode(callback),
                ["code_verifier"] = authorizationRequest.CodeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = authorizationRequest.RedirectUri.AbsoluteUri,
            }),
            cancellationToken).ConfigureAwait(false);
        var token = await ReadTokenAsync(response, null, cancellationToken).ConfigureAwait(false);
        await PersistAsync(token, cancellationToken).ConfigureAwait(false);
        return token;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        current ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null && current.ExpiresAtUtc > clock.GetUtcNow().AddMinutes(2))
        {
            return current.AccessToken;
        }

        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (current is not null && current.ExpiresAtUtc > clock.GetUtcNow().AddMinutes(2))
            {
                return current.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(current?.RefreshToken))
            {
                throw new InvalidOperationException("YouTube authorization is required.");
            }

            using var response = await httpClient.PostAsync(
                GoogleInstalledAppAuthorization.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = configuration.ClientId,
                    ["refresh_token"] = current.RefreshToken,
                    ["grant_type"] = "refresh_token",
                }),
                cancellationToken).ConfigureAwait(false);
            var refreshed = await ReadTokenAsync(response, current.RefreshToken, cancellationToken).ConfigureAwait(false);
            await PersistAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        current = null;
        await secretStore.DeleteAsync(accountStorageKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoogleTokenSet?> LoadAsync(CancellationToken cancellationToken)
    {
        var bytes = await secretStore.LoadAsync(accountStorageKey, cancellationToken).ConfigureAwait(false);
        return bytes is null
            ? null
            : JsonSerializer.Deserialize<GoogleTokenSet>(bytes)
              ?? throw new InvalidDataException("Stored Google authorization is invalid.");
    }

    private async Task PersistAsync(GoogleTokenSet token, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(token);
        try
        {
            await secretStore.SaveAsync(accountStorageKey, bytes, cancellationToken).ConfigureAwait(false);
            current = token;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<GoogleTokenSet> ReadTokenAsync(
        HttpResponseMessage response,
        string? existingRefreshToken,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        var value = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Google returned no token response.");
        var refreshToken = value.RefreshToken ?? existingRefreshToken;
        ValidateGrantedScopes(value.Scope);
        return new(
            value.AccessToken,
            refreshToken,
            clock.GetUtcNow().AddSeconds(Math.Max(0, value.ExpiresIn)),
            value.Scope);
    }

    private static void ValidateGrantedScopes(string scopes)
    {
        var values = scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (!values.Contains(GoogleInstalledAppAuthorization.UploadScope)
            || !values.Contains(GoogleInstalledAppAuthorization.ChannelReadScope))
        {
            throw new InvalidOperationException("Google did not grant the YouTube upload and channel-read scopes ADFi requires.");
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string Scope);

    public void Dispose() => refreshLock.Dispose();
}
