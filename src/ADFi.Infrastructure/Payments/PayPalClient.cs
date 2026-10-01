using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADFi.Core;

namespace ADFi.Infrastructure.Payments;

public sealed record PayPalConfiguration(
    Uri ApiBaseUri,
    string ClientId,
    string ClientSecret,
    string WebhookId,
    string CommunityPlanId,
    string CreatorPlanId);

public sealed record PayPalWebhookHeaders(
    string TransmissionId,
    string TransmissionTime,
    string TransmissionSignature,
    string CertificateUrl,
    string AuthenticationAlgorithm);

public sealed record PayPalSubscriptionApproval(
    string SubscriptionId,
    Uri ApprovalUrl,
    SubscriptionPlan Plan);

public sealed record PayPalPayoutSubmission(
    string ProviderBatchId,
    string ProviderItemId,
    string Status);

public interface IPayPalProvider
{
    Task<PayPalSubscriptionApproval> CreateSubscriptionAsync(
        Guid userId,
        SubscriptionPlan plan,
        Uri returnUrl,
        Uri cancelUrl,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<bool> VerifyWebhookAsync(
        PayPalWebhookHeaders headers,
        JsonElement webhookEvent,
        CancellationToken cancellationToken);

    Task<PayPalPayoutSubmission> SubmitPayoutAsync(
        string senderBatchId,
        string senderItemId,
        string verifiedPayPalEmail,
        decimal amountUsd,
        string note,
        CancellationToken cancellationToken);
}

/// <summary>Server-only PayPal REST client. Client credentials must come from protected server configuration.</summary>
public sealed class PayPalClient(
    HttpClient httpClient,
    PayPalConfiguration configuration,
    TimeProvider? timeProvider = null) : IPayPalProvider, IDisposable
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private string? cachedAccessToken;
    private DateTimeOffset tokenExpiresAtUtc;

    public async Task<PayPalSubscriptionApproval> CreateSubscriptionAsync(
        Guid userId,
        SubscriptionPlan plan,
        Uri returnUrl,
        Uri cancelUrl,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ValidateHttps(returnUrl, nameof(returnUrl));
        ValidateHttps(cancelUrl, nameof(cancelUrl));
        var providerPlanId = plan switch
        {
            SubscriptionPlan.Community => configuration.CommunityPlanId,
            SubscriptionPlan.Creator => configuration.CreatorPlanId,
            _ => throw new ArgumentOutOfRangeException(nameof(plan)),
        };

        using var request = await AuthorizedRequestAsync(HttpMethod.Post, "v1/billing/subscriptions", cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Add("PayPal-Request-Id", idempotencyKey);
        request.Content = JsonContent.Create(new
        {
            plan_id = providerPlanId,
            custom_id = userId.ToString("D"),
            application_context = new
            {
                user_action = "SUBSCRIBE_NOW",
                return_url = returnUrl.AbsoluteUri,
                cancel_url = cancelUrl.AbsoluteUri,
            },
        });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "PAYPAL_SUBSCRIPTION_CREATE_FAILED", cancellationToken).ConfigureAwait(false);
        var value = await response.Content.ReadFromJsonAsync<SubscriptionResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("PayPal returned no subscription result.");
        var approval = value.Links.FirstOrDefault(link => link.Rel == "approve")?.Href
                       ?? throw new InvalidOperationException("PayPal returned no subscription approval URL.");
        return new(value.Id, new Uri(approval), plan);
    }

    public async Task<bool> VerifyWebhookAsync(
        PayPalWebhookHeaders headers,
        JsonElement webhookEvent,
        CancellationToken cancellationToken)
    {
        ValidateWebhookHeaders(headers);
        using var request = await AuthorizedRequestAsync(
            HttpMethod.Post,
            "v1/notifications/verify-webhook-signature",
            cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(new
        {
            transmission_id = headers.TransmissionId,
            transmission_time = headers.TransmissionTime,
            cert_url = headers.CertificateUrl,
            auth_algo = headers.AuthenticationAlgorithm,
            transmission_sig = headers.TransmissionSignature,
            webhook_id = configuration.WebhookId,
            webhook_event = webhookEvent,
        });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "PAYPAL_WEBHOOK_VERIFY_FAILED", cancellationToken).ConfigureAwait(false);
        var value = await response.Content.ReadFromJsonAsync<WebhookVerificationResponse>(cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(value?.VerificationStatus, "SUCCESS", StringComparison.Ordinal);
    }

    public async Task<PayPalPayoutSubmission> SubmitPayoutAsync(
        string senderBatchId,
        string senderItemId,
        string verifiedPayPalEmail,
        decimal amountUsd,
        string note,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderBatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedPayPalEmail);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountUsd);

        using var request = await AuthorizedRequestAsync(HttpMethod.Post, "v1/payments/payouts", cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Add("PayPal-Request-Id", senderBatchId);
        request.Content = JsonContent.Create(new
        {
            sender_batch_header = new
            {
                sender_batch_id = senderBatchId,
                email_subject = "Your ADFi payout",
                email_message = "Your requested ADFi payout has been sent.",
            },
            items = new[]
            {
                new
                {
                    recipient_type = "EMAIL",
                    amount = new
                    {
                        value = amountUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                        currency = "USD",
                    },
                    receiver = verifiedPayPalEmail,
                    note,
                    sender_item_id = senderItemId,
                },
            },
        });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "PAYPAL_PAYOUT_FAILED", cancellationToken).ConfigureAwait(false);
        var value = await response.Content.ReadFromJsonAsync<PayoutResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("PayPal returned no payout result.");
        var itemId = value.Items?.SingleOrDefault()?.PayoutItemId ?? senderItemId;
        return new(value.BatchHeader.PayoutBatchId, itemId, value.BatchHeader.BatchStatus);
    }

    private async Task<HttpRequestMessage> AuthorizedRequestAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, new Uri(configuration.ApiBaseUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (cachedAccessToken is not null && tokenExpiresAtUtc > clock.GetUtcNow().AddMinutes(1))
        {
            return cachedAccessToken;
        }

        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cachedAccessToken is not null && tokenExpiresAtUtc > clock.GetUtcNow().AddMinutes(1))
            {
                return cachedAccessToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(configuration.ApiBaseUri, "v1/oauth2/token"));
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{configuration.ClientId}:{configuration.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
            });
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "PAYPAL_AUTH_FAILED", cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("PayPal returned no access token.");
            cachedAccessToken = result.AccessToken;
            tokenExpiresAtUtc = clock.GetUtcNow().AddSeconds(Math.Max(0, result.ExpiresIn - 60));
            return cachedAccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static void ValidateWebhookHeaders(PayPalWebhookHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (new[]
            {
                headers.TransmissionId,
                headers.TransmissionTime,
                headers.TransmissionSignature,
                headers.CertificateUrl,
                headers.AuthenticationAlgorithm,
            }.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("All PayPal webhook verification headers are required.", nameof(headers));
        }

        if (!Uri.TryCreate(headers.CertificateUrl, UriKind.Absolute, out var certificate)
            || certificate.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("PayPal certificate URL must be HTTPS.", nameof(headers));
        }
    }

    private static void ValidateHttps(Uri uri, string parameterName)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("PayPal redirect URLs must use HTTPS.", parameterName);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string code,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        throw new InvalidOperationException($"{code}: HTTP {(int)response.StatusCode}; response SHA-256 {hash}.");
    }

    private sealed record AccessTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record SubscriptionResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("links")] IReadOnlyList<PayPalLink> Links);

    private sealed record PayPalLink(
        [property: JsonPropertyName("href")] string Href,
        [property: JsonPropertyName("rel")] string Rel);

    private sealed record WebhookVerificationResponse(
        [property: JsonPropertyName("verification_status")] string VerificationStatus);

    private sealed record PayoutResponse(
        [property: JsonPropertyName("batch_header")] PayoutBatchHeader BatchHeader,
        [property: JsonPropertyName("items")] IReadOnlyList<PayoutItem>? Items);

    private sealed record PayoutBatchHeader(
        [property: JsonPropertyName("payout_batch_id")] string PayoutBatchId,
        [property: JsonPropertyName("batch_status")] string BatchStatus);

    private sealed record PayoutItem([property: JsonPropertyName("payout_item_id")] string PayoutItemId);

    public void Dispose() => tokenLock.Dispose();
}
