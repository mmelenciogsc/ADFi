using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ADFi.Infrastructure.Payments;

public enum PayPalEventEffect
{
    SubscriptionCreated,
    SubscriptionActivated,
    SubscriptionUpdated,
    SubscriptionSuspended,
    SubscriptionCancelled,
    SubscriptionExpired,
    SubscriptionPaymentFailed,
    SubscriptionPaymentCompleted,
    Refund,
    Reversal,
    PayoutStatusChanged,
    Unsupported,
}

public sealed record PayPalEventEnvelope(
    string EventId,
    string EventType,
    DateTimeOffset? ProviderCreatedAtUtc,
    string? SubscriptionId,
    PayPalEventEffect Effect,
    string PayloadSha256,
    JsonElement Payload);

public static class PayPalWebhookEventParser
{
    public static PayPalEventEnvelope Parse(string rawJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var id = RequiredString(root, "id");
        var type = RequiredString(root, "event_type");
        DateTimeOffset? created = null;
        if (root.TryGetProperty("create_time", out var time)
            && time.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(time.GetString(), out var parsed))
        {
            created = parsed;
        }

        var subscriptionId = FindSubscriptionId(root, type);
        return new(
            id,
            type,
            created,
            subscriptionId,
            MapEffect(type),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))),
            root.Clone());
    }

    private static string? FindSubscriptionId(JsonElement root, string type)
    {
        if (!root.TryGetProperty("resource", out var resource))
        {
            return null;
        }

        if (type.StartsWith("BILLING.SUBSCRIPTION.", StringComparison.Ordinal)
            && resource.TryGetProperty("id", out var directId))
        {
            return directId.GetString();
        }

        return resource.TryGetProperty("billing_agreement_id", out var billingId)
            ? billingId.GetString()
            : null;
    }

    private static PayPalEventEffect MapEffect(string eventType) => eventType switch
    {
        "BILLING.SUBSCRIPTION.CREATED" => PayPalEventEffect.SubscriptionCreated,
        "BILLING.SUBSCRIPTION.ACTIVATED" => PayPalEventEffect.SubscriptionActivated,
        "BILLING.SUBSCRIPTION.UPDATED" => PayPalEventEffect.SubscriptionUpdated,
        "BILLING.SUBSCRIPTION.SUSPENDED" => PayPalEventEffect.SubscriptionSuspended,
        "BILLING.SUBSCRIPTION.CANCELLED" => PayPalEventEffect.SubscriptionCancelled,
        "BILLING.SUBSCRIPTION.EXPIRED" => PayPalEventEffect.SubscriptionExpired,
        "BILLING.SUBSCRIPTION.PAYMENT.FAILED" => PayPalEventEffect.SubscriptionPaymentFailed,
        "PAYMENT.SALE.COMPLETED" => PayPalEventEffect.SubscriptionPaymentCompleted,
        "PAYMENT.SALE.REFUNDED" => PayPalEventEffect.Refund,
        "PAYMENT.SALE.REVERSED" => PayPalEventEffect.Reversal,
        "PAYMENT.PAYOUTSBATCH.SUCCESS" or
        "PAYMENT.PAYOUTSBATCH.DENIED" or
        "PAYMENT.PAYOUTS-ITEM.SUCCEEDED" or
        "PAYMENT.PAYOUTS-ITEM.FAILED" or
        "PAYMENT.PAYOUTS-ITEM.RETURNED" => PayPalEventEffect.PayoutStatusChanged,
        _ => PayPalEventEffect.Unsupported,
    };

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"PayPal webhook is missing {propertyName}.");
}

public interface IPayPalWebhookEventStore
{
    Task<bool> TryBeginAsync(PayPalEventEnvelope webhookEvent, CancellationToken cancellationToken);

    Task ApplyAsync(PayPalEventEnvelope webhookEvent, CancellationToken cancellationToken);

    Task MarkFailedAsync(string eventId, string failureCode, CancellationToken cancellationToken);
}

public sealed class PayPalWebhookProcessor(IPayPalProvider provider, IPayPalWebhookEventStore store)
{
    public async Task<bool> ProcessAsync(
        PayPalWebhookHeaders headers,
        string rawJson,
        CancellationToken cancellationToken)
    {
        var parsed = PayPalWebhookEventParser.Parse(rawJson);
        if (!await provider.VerifyWebhookAsync(headers, parsed.Payload, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PAYPAL_WEBHOOK_SIGNATURE_INVALID");
        }

        if (!await store.TryBeginAsync(parsed, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await store.ApplyAsync(parsed, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await store.MarkFailedAsync(parsed.EventId, "PAYPAL_EVENT_APPLY_FAILED", cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }
}
