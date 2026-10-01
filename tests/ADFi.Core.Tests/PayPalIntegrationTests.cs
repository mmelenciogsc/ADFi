using System.Net;
using System.Text;
using System.Text.Json;
using ADFi.Core;
using ADFi.Infrastructure.Payments;

namespace ADFi.Core.Tests;

public sealed class PayPalIntegrationTests
{
    [Fact]
    public async Task CommunitySubscriptionUsesConfiguredTwoDollarPlanAndIdempotency()
    {
        var handler = new PayPalHandler();
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        var result = await client.CreateSubscriptionAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SubscriptionPlan.Community,
            new Uri("https://adfi.example/paypal/return"),
            new Uri("https://adfi.example/paypal/cancel"),
            "request-1",
            TestContext.Current.CancellationToken);

        Assert.Equal("subscription-1", result.SubscriptionId);
        Assert.Equal(SubscriptionPlan.Community, result.Plan);
        Assert.Contains("community-plan", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Equal("request-1", handler.PayPalRequestIds.Single());
    }

    [Fact]
    public async Task WebhookProcessorVerifiesBeforeApplyingAndSuppressesDuplicate()
    {
        var provider = new AcceptingProvider();
        var store = new RecordingWebhookStore();
        var processor = new PayPalWebhookProcessor(provider, store);
        const string json = "{\"id\":\"WH-1\",\"event_type\":\"BILLING.SUBSCRIPTION.ACTIVATED\",\"create_time\":\"2026-10-02T00:00:00Z\",\"resource\":{\"id\":\"I-123\"}}";
        var headers = Headers();

        Assert.True(await processor.ProcessAsync(headers, json, TestContext.Current.CancellationToken));
        Assert.False(await processor.ProcessAsync(headers, json, TestContext.Current.CancellationToken));
        Assert.Equal([PayPalEventEffect.SubscriptionActivated], store.Applied);
        Assert.Equal(2, provider.VerificationCount);
    }

    [Fact]
    public void PaymentEventMapsToItsSubscriptionWithoutTrustingDeliveryOrder()
    {
        var parsed = PayPalWebhookEventParser.Parse(
            "{\"id\":\"WH-2\",\"event_type\":\"PAYMENT.SALE.COMPLETED\",\"resource\":{\"billing_agreement_id\":\"I-ABC\"}}");
        Assert.Equal(PayPalEventEffect.SubscriptionPaymentCompleted, parsed.Effect);
        Assert.Equal("I-ABC", parsed.SubscriptionId);
        Assert.Equal(64, parsed.PayloadSha256.Length);
    }

    [Fact]
    public async Task PayoutUsesImmutableBatchAndItemIdentifiers()
    {
        var handler = new PayPalHandler();
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        var result = await client.SubmitPayoutAsync(
            "batch-1", "item-1", "verified@example.test", 12.34m, "ADFi withdrawal",
            TestContext.Current.CancellationToken);

        Assert.Equal("batch-1", handler.PayPalRequestIds.Single());
        Assert.Equal("provider-batch-1", result.ProviderBatchId);
        Assert.Contains("\"value\":\"12.34\"", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"sender_item_id\":\"item-1\"", handler.Bodies[1], StringComparison.Ordinal);
    }

    private static PayPalClient CreateClient(HttpClient http) => new(
        http,
        new PayPalConfiguration(
            new Uri("https://api-m.sandbox.paypal.com/"),
            "client-id", "server-secret", "webhook-1", "community-plan", "creator-plan"),
        new FixedTimeProvider());

    private static PayPalWebhookHeaders Headers() =>
        new("transmission", "2026-10-02T00:00:00Z", "signature", "https://api.paypal.com/cert", "SHA256withRSA");

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class PayPalHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> PayPalRequestIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.Headers.TryGetValues("PayPal-Request-Id", out var ids))
            {
                PayPalRequestIds.Add(ids.Single());
            }

            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/v1/oauth2/token" => Json("{\"access_token\":\"access\",\"expires_in\":3600}"),
                "/v1/billing/subscriptions" => Json("{\"id\":\"subscription-1\",\"links\":[{\"href\":\"https://paypal.example/approve\",\"rel\":\"approve\"}]}"),
                "/v1/payments/payouts" => Json("{\"batch_header\":{\"payout_batch_id\":\"provider-batch-1\",\"batch_status\":\"PENDING\"},\"items\":[{\"payout_item_id\":\"provider-item-1\"}]}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class AcceptingProvider : IPayPalProvider
    {
        public int VerificationCount { get; private set; }

        public Task<PayPalSubscriptionApproval> CreateSubscriptionAsync(Guid userId, SubscriptionPlan plan, Uri returnUrl, Uri cancelUrl, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> VerifyWebhookAsync(PayPalWebhookHeaders headers, JsonElement webhookEvent, CancellationToken cancellationToken)
        {
            VerificationCount++;
            return Task.FromResult(true);
        }

        public Task<PayPalPayoutSubmission> SubmitPayoutAsync(string senderBatchId, string senderItemId, string verifiedPayPalEmail, decimal amountUsd, string note, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingWebhookStore : IPayPalWebhookEventStore
    {
        private readonly HashSet<string> events = new(StringComparer.Ordinal);
        public List<PayPalEventEffect> Applied { get; } = [];

        public Task<bool> TryBeginAsync(PayPalEventEnvelope webhookEvent, CancellationToken cancellationToken) =>
            Task.FromResult(events.Add(webhookEvent.EventId));

        public Task ApplyAsync(PayPalEventEnvelope webhookEvent, CancellationToken cancellationToken)
        {
            Applied.Add(webhookEvent.Effect);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(string eventId, string failureCode, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
