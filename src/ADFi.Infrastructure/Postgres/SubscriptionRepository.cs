using ADFi.Core;
using ADFi.Infrastructure.Payments;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace ADFi.Infrastructure.Postgres;

public sealed record AccountEntitlement(
    SubscriptionPlan? Plan,
    string Status,
    bool CanCreate,
    bool CanConversationallyEdit);

public sealed class SubscriptionRepository(AdfiDatabase database, TimeProvider timeProvider)
{
    public async Task RecordPendingAsync(
        Guid userId,
        SubscriptionPlan plan,
        string providerSubscriptionId,
        CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            """
            INSERT INTO subscriptions
                (id, user_id, plan_code, provider, provider_subscription_id, status, created_at_utc, updated_at_utc)
            VALUES ($1, $2, $3, 'PAYPAL', $4, 'APPROVAL_PENDING', $5, $5)
            ON CONFLICT (provider_subscription_id) DO NOTHING
            """);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(plan.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue(providerSubscriptionId);
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // PayPal can deliver activation/payment webhooks before the browser approval flow has
        // persisted its local subscription row. Re-evaluate those verified inbox records now.
        await new PostgresPayPalWebhookEventStore(database, timeProvider)
            .ReplayWaitingForSubscriptionAsync(providerSubscriptionId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AccountEntitlement> GetEntitlementAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            """
            SELECT plan_code, status
            FROM subscriptions
            WHERE user_id = $1
            ORDER BY updated_at_utc DESC
            LIMIT 1
            """);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(null, "NONE", false, false);
        }

        var status = reader.GetString(1);
        if (!string.Equals(status, "ACTIVE", StringComparison.Ordinal))
        {
            return new(null, status, false, false);
        }

        var plan = reader.GetString(0) switch
        {
            "COMMUNITY" => SubscriptionPlan.Community,
            "CREATOR" => SubscriptionPlan.Creator,
            _ => throw new InvalidDataException("Stored subscription plan is unknown."),
        };
        return new(plan, status, Entitlements.CanCreate(plan), Entitlements.CanEditConversationally(plan));
    }
}

public sealed class PostgresPayPalWebhookEventStore(
    AdfiDatabase database,
    TimeProvider timeProvider) : IPayPalWebhookEventStore
{
    public async Task<bool> TryBeginAsync(
        PayPalEventEnvelope webhookEvent,
        CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            """
            INSERT INTO paypal_webhook_inbox
                (provider_event_id, event_type, provider_subscription_id, provider_created_at_utc,
                 payload_sha256, payload_json, processing_status, received_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, 'PROCESSING', $7)
            ON CONFLICT (provider_event_id) DO NOTHING
            """);
        command.Parameters.AddWithValue(webhookEvent.EventId);
        command.Parameters.AddWithValue(webhookEvent.EventType);
        AddNullable(command, NpgsqlDbType.Text, webhookEvent.SubscriptionId);
        AddNullable(command, NpgsqlDbType.TimestampTz, webhookEvent.ProviderCreatedAtUtc);
        command.Parameters.AddWithValue(webhookEvent.PayloadSha256);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, webhookEvent.Payload.GetRawText());
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task ApplyAsync(PayPalEventEnvelope webhookEvent, CancellationToken cancellationToken)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var subscriptionId = await FindSubscriptionAsync(
            connection,
            transaction,
            webhookEvent.SubscriptionId,
            cancellationToken).ConfigureAwait(false);
        if (subscriptionId is null && webhookEvent.SubscriptionId is not null)
        {
            await SetInboxStatusAsync(
                connection, transaction, webhookEvent.EventId, "WAITING_FOR_SUBSCRIPTION", null, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (subscriptionId is not null)
        {
            await RecordSubscriptionEventAsync(
                connection, transaction, subscriptionId.Value, webhookEvent, cancellationToken)
                .ConfigureAwait(false);
            var status = DeriveSubscriptionStatus(webhookEvent);
            if (status is not null)
            {
                await UpdateSubscriptionStatusAsync(
                    connection,
                    transaction,
                    subscriptionId.Value,
                    status,
                    webhookEvent,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await SetInboxStatusAsync(
            connection,
            transaction,
            webhookEvent.EventId,
            "COMPLETED",
            webhookEvent.Effect == PayPalEventEffect.Unsupported ? "UNSUPPORTED_IGNORED" : null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ReplayWaitingForSubscriptionAsync(
        string providerSubscriptionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSubscriptionId);
        var pending = new List<string>();
        await using (var command = database.DataSource.CreateCommand(
                         """
                         SELECT payload_json::text
                         FROM paypal_webhook_inbox
                         WHERE provider_subscription_id = $1
                           AND processing_status = 'WAITING_FOR_SUBSCRIPTION'
                         ORDER BY provider_created_at_utc NULLS FIRST, received_at_utc, provider_event_id
                         """))
        {
            command.Parameters.AddWithValue(providerSubscriptionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pending.Add(reader.GetString(0));
            }
        }

        foreach (var rawJson in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var webhookEvent = PayPalWebhookEventParser.Parse(rawJson);
            if (!string.Equals(webhookEvent.SubscriptionId, providerSubscriptionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Stored PayPal webhook subscription identity changed during replay.");
            }

            // Signature verification happened before the immutable payload entered this inbox.
            // ApplyAsync remains timestamp-monotonic and idempotent at the event/status rows.
            await ApplyAsync(webhookEvent, cancellationToken).ConfigureAwait(false);
        }

        return pending.Count;
    }

    public async Task MarkFailedAsync(
        string eventId,
        string failureCode,
        CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            """
            UPDATE paypal_webhook_inbox
            SET processing_status = 'FAILED', failure_code = $2, processed_at_utc = now()
            WHERE provider_event_id = $1
            """);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(failureCode);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid?> FindSubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string? providerSubscriptionId,
        CancellationToken cancellationToken)
    {
        if (providerSubscriptionId is null)
        {
            return null;
        }

        await using var command = new NpgsqlCommand(
            "SELECT id FROM subscriptions WHERE provider_subscription_id = $1 FOR UPDATE",
            connection,
            transaction);
        command.Parameters.AddWithValue(providerSubscriptionId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid id ? id : null;
    }

    private static async Task RecordSubscriptionEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subscriptionId,
        PayPalEventEnvelope webhookEvent,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO subscription_events
                (id, subscription_id, provider_event_id, event_type, provider_created_at_utc,
                 received_at_utc, payload_sha256, processed_at_utc, processing_result)
            VALUES ($1, $2, $3, $4, $5, now(), $6, now(), $7)
            ON CONFLICT (provider_event_id) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(subscriptionId);
        command.Parameters.AddWithValue(webhookEvent.EventId);
        command.Parameters.AddWithValue(webhookEvent.EventType);
        AddNullable(command, NpgsqlDbType.TimestampTz, webhookEvent.ProviderCreatedAtUtc);
        command.Parameters.AddWithValue(webhookEvent.PayloadSha256);
        command.Parameters.AddWithValue(webhookEvent.Effect.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateSubscriptionStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subscriptionId,
        string status,
        PayPalEventEnvelope webhookEvent,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        var effectiveAt = webhookEvent.ProviderCreatedAtUtc ?? receivedAtUtc;
        await using var command = new NpgsqlCommand(
            """
            UPDATE subscriptions
            SET status = $2, provider_status_at_utc = $3, updated_at_utc = now()
            WHERE id = $1
              AND (provider_status_at_utc IS NULL OR provider_status_at_utc <= $3)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(subscriptionId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(effectiveAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetInboxStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventId,
        string status,
        string? result,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE paypal_webhook_inbox
            SET processing_status = $2, processed_at_utc = now(), failure_code = $3
            WHERE provider_event_id = $1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(status);
        AddNullable(command, NpgsqlDbType.Text, result);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? DeriveSubscriptionStatus(PayPalEventEnvelope value) => value.Effect switch
    {
        PayPalEventEffect.SubscriptionCreated => "APPROVAL_PENDING",
        PayPalEventEffect.SubscriptionActivated => "ACTIVE",
        PayPalEventEffect.SubscriptionUpdated => ReadResourceStatus(value.Payload),
        PayPalEventEffect.SubscriptionSuspended => "SUSPENDED",
        PayPalEventEffect.SubscriptionCancelled => "CANCELLED",
        PayPalEventEffect.SubscriptionExpired => "EXPIRED",
        PayPalEventEffect.SubscriptionPaymentFailed => "PAYMENT_FAILED",
        _ => null,
    };

    private static string? ReadResourceStatus(JsonElement payload) =>
        payload.TryGetProperty("resource", out var resource)
        && resource.TryGetProperty("status", out var status)
        && status.ValueKind == JsonValueKind.String
            ? status.GetString()?.ToUpperInvariant()
            : null;

    private static void AddNullable(NpgsqlCommand command, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });
}
