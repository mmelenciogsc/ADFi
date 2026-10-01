using ADFi.Core;
using ADFi.Infrastructure.Payments;
using ADFi.Infrastructure.Postgres;

namespace ADFi.Api;

public static class PaymentEndpoints
{
    private const long MaximumWebhookBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapPaymentEndpoints(
        this IEndpointRouteBuilder endpoints,
        Uri publicBaseUri)
    {
        endpoints.MapPost("/api/v1/subscriptions/checkout", async (
                CheckoutRequest checkout,
                HttpRequest request,
                AccountRepository accounts,
                SubscriptionRepository subscriptions,
                IPayPalProvider payPal,
                CancellationToken cancellationToken) =>
            {
                var account = await AuthenticateAsync(request, accounts, cancellationToken).ConfigureAwait(false);
                if (account is null)
                {
                    return Results.Unauthorized();
                }

                if (!Enum.TryParse<SubscriptionPlan>(checkout.Plan, ignoreCase: true, out var plan)
                    || plan == SubscriptionPlan.None)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        [nameof(checkout.Plan)] = ["Plan must be Community or Creator."],
                    });
                }

                var requestId = $"subscription-{account.UserId:N}-{Guid.NewGuid():N}";
                var approval = await payPal.CreateSubscriptionAsync(
                    account.UserId,
                    plan,
                    new Uri(publicBaseUri, "/billing/paypal/return"),
                    new Uri(publicBaseUri, "/billing/paypal/cancel"),
                    requestId,
                    cancellationToken).ConfigureAwait(false);
                await subscriptions.RecordPendingAsync(
                    account.UserId,
                    plan,
                    approval.SubscriptionId,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(new
                {
                    approval.SubscriptionId,
                    approval.ApprovalUrl,
                    Plan = plan.ToString(),
                });
            })
            .RequireRateLimiting("authentication");

        endpoints.MapGet("/api/v1/subscriptions/entitlements", async (
                HttpRequest request,
                AccountRepository accounts,
                SubscriptionRepository subscriptions,
                CancellationToken cancellationToken) =>
            {
                var account = await AuthenticateAsync(request, accounts, cancellationToken).ConfigureAwait(false);
                return account is null
                    ? Results.Unauthorized()
                    : Results.Ok(await subscriptions.GetEntitlementAsync(account.UserId, cancellationToken)
                        .ConfigureAwait(false));
            })
            .RequireRateLimiting("authentication");

        endpoints.MapPost("/api/v1/paypal/webhook", async (
                HttpRequest request,
                PayPalWebhookProcessor processor,
                CancellationToken cancellationToken) =>
            {
                if (request.ContentLength is > MaximumWebhookBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                using var reader = new StreamReader(request.Body);
                var rawJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (System.Text.Encoding.UTF8.GetByteCount(rawJson) > MaximumWebhookBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var headers = new PayPalWebhookHeaders(
                    RequiredHeader(request, "PAYPAL-TRANSMISSION-ID"),
                    RequiredHeader(request, "PAYPAL-TRANSMISSION-TIME"),
                    RequiredHeader(request, "PAYPAL-TRANSMISSION-SIG"),
                    RequiredHeader(request, "PAYPAL-CERT-URL"),
                    RequiredHeader(request, "PAYPAL-AUTH-ALGO"));
                var applied = await processor.ProcessAsync(headers, rawJson, cancellationToken).ConfigureAwait(false);
                return Results.Ok(new { accepted = true, applied });
            })
            .RequireRateLimiting("authentication")
            .AllowAnonymous();

        return endpoints;
    }

    private static async Task<AuthenticatedAccount?> AuthenticateAsync(
        HttpRequest request,
        AccountRepository accounts,
        CancellationToken cancellationToken)
    {
        const string prefix = "Bearer ";
        var value = request.Headers.Authorization.ToString();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = value[prefix.Length..].Trim();
        return token.Length < 32
            ? null
            : await accounts.ValidateSessionAsync(token, cancellationToken).ConfigureAwait(false);
    }

    private static string RequiredHeader(HttpRequest request, string name)
    {
        var value = request.Headers[name].ToString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new BadHttpRequestException($"Required PayPal header is missing: {name}")
            : value;
    }
}

public sealed record CheckoutRequest(string Plan);
