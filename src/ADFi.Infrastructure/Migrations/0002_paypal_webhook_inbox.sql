ALTER TABLE subscriptions
    ADD COLUMN provider_status_at_utc timestamptz NULL;

CREATE TABLE paypal_webhook_inbox (
    provider_event_id text PRIMARY KEY,
    event_type text NOT NULL,
    provider_subscription_id text NULL,
    provider_created_at_utc timestamptz NULL,
    payload_sha256 text NOT NULL,
    payload_json jsonb NOT NULL,
    processing_status text NOT NULL CHECK (processing_status IN ('PROCESSING', 'WAITING_FOR_SUBSCRIPTION', 'COMPLETED', 'FAILED')),
    attempt_count integer NOT NULL DEFAULT 1 CHECK (attempt_count > 0),
    received_at_utc timestamptz NOT NULL,
    processed_at_utc timestamptz NULL,
    failure_code text NULL
);

CREATE INDEX ix_paypal_webhook_waiting
    ON paypal_webhook_inbox(provider_subscription_id, received_at_utc)
    WHERE processing_status = 'WAITING_FOR_SUBSCRIPTION';
