using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ADFi.Core;
using ADFi.Web;

namespace ADFi.Desktop;

public sealed record DesktopAccount(Guid UserId, string Email, string DisplayName, bool EmailVerified);

public sealed record DesktopSession(
    string Token,
    DateTimeOffset ExpiresAtUtc,
    DesktopAccount Account);

public sealed record DesktopEntitlement(
    SubscriptionPlan? Plan,
    string Status,
    bool CanCreate,
    bool CanConversationallyEdit);

public sealed record RegistrationReceipt(Guid UserId, string Message);

public sealed class AdfiApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class AdfiApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<DesktopSession> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/account/login",
            new { email, password, deviceLabel = $"ADFi desktop on {Environment.MachineName}" },
            null,
            cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<DesktopSession>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RegistrationReceipt> RegisterAsync(
        string email,
        string displayName,
        string password,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/account/register",
            new { email, displayName, password },
            null,
            cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<RegistrationReceipt>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RequestPasswordResetAsync(
        string email,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/account/password-reset/request",
            new { email },
            null,
            cancellationToken).ConfigureAwait(false);
        var result = await ReadRequiredAsync<MessageResponse>(response, cancellationToken).ConfigureAwait(false);
        return result.Message;
    }

    public async Task<DesktopEntitlement> GetEntitlementAsync(
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "api/v1/subscriptions/entitlements",
            null,
            token,
            cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<DesktopEntitlement>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiscoverSurface>> GetDiscoverAsync(
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "api/v1/discover",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        var projects = await ReadRequiredAsync<DiscoverSurface[]>(response, cancellationToken).ConfigureAwait(false);
        return projects;
    }

    public async Task LogoutAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/account/logout",
            new { },
            token,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        object? content,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, relativePath);
            if (content is not null)
            {
                request.Content = JsonContent.Create(content, options: JsonOptions);
            }

            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            var statusCode = response.StatusCode;
            response.Dispose();
            throw new AdfiApiException(error, statusCode);
        }
        catch (AdfiApiException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new AdfiApiException(
                "ADFi could not establish a secure connection to the server. Check the network and trusted HTTPS certificate.",
                null,
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AdfiApiException("The ADFi server did not respond in time.", null, exception);
        }
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return result ?? throw new AdfiApiException(
            "The ADFi server returned an empty or invalid response.",
            response.StatusCode);
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }

            if (!string.IsNullOrWhiteSpace(problem?.Message))
            {
                return problem.Message;
            }

            if (problem?.Errors is { Count: > 0 })
            {
                return string.Join(" ", problem.Errors.Values.SelectMany(value => value));
            }
        }
        catch (JsonException)
        {
            // The status code remains authoritative when a proxy returns a non-JSON error page.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The email address or password was not accepted.",
            HttpStatusCode.TooManyRequests => "Too many requests. Wait a moment before trying again.",
            HttpStatusCode.ServiceUnavailable => "This ADFi service is temporarily unavailable.",
            _ => $"The ADFi server returned {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
    }

    private sealed record MessageResponse(string Message);

    private sealed record ApiProblem(
        string? Detail,
        string? Message,
        Dictionary<string, string[]>? Errors);
}
