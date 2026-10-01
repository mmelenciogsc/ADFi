CREATE TABLE users (
    id uuid PRIMARY KEY,
    email_normalized text NOT NULL UNIQUE,
    email_display text NOT NULL,
    display_name text NOT NULL,
    password_hash text NOT NULL,
    email_verified_at_utc timestamptz NULL,
    created_at_utc timestamptz NOT NULL,
    disabled_at_utc timestamptz NULL
);

CREATE TABLE email_verification_tokens (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_sha256 text NOT NULL UNIQUE,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    consumed_at_utc timestamptz NULL
);

CREATE TABLE password_reset_tokens (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_sha256 text NOT NULL UNIQUE,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    consumed_at_utc timestamptz NULL
);

CREATE TABLE devices (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_fingerprint_hash text NULL,
    display_name text NOT NULL,
    first_seen_at_utc timestamptz NOT NULL,
    last_seen_at_utc timestamptz NOT NULL,
    revoked_at_utc timestamptz NULL
);

CREATE TABLE auth_sessions (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id uuid NULL REFERENCES devices(id) ON DELETE SET NULL,
    token_sha256 text NOT NULL UNIQUE,
    device_label text NOT NULL,
    created_at_utc timestamptz NOT NULL,
    expires_at_utc timestamptz NOT NULL,
    rotated_at_utc timestamptz NULL,
    revoked_at_utc timestamptz NULL
);

CREATE TABLE subscription_plans (
    code text PRIMARY KEY,
    display_name text NOT NULL,
    monthly_price_usd_cents integer NOT NULL CHECK (monthly_price_usd_cents > 0),
    can_create boolean NOT NULL,
    can_conversationally_edit boolean NOT NULL,
    active boolean NOT NULL
);

INSERT INTO subscription_plans(code, display_name, monthly_price_usd_cents, can_create, can_conversationally_edit, active)
VALUES
    ('COMMUNITY', 'Community', 200, false, false, true),
    ('CREATOR', 'Creator', 900, true, true, true);

CREATE TABLE subscriptions (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id),
    plan_code text NOT NULL REFERENCES subscription_plans(code),
    provider text NOT NULL CHECK (provider = 'PAYPAL'),
    provider_subscription_id text NOT NULL UNIQUE,
    status text NOT NULL,
    current_period_start_utc timestamptz NULL,
    current_period_end_utc timestamptz NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL
);

CREATE TABLE paypal_references (
    id uuid PRIMARY KEY,
    user_id uuid NULL REFERENCES users(id),
    reference_type text NOT NULL,
    provider_reference text NOT NULL UNIQUE,
    created_at_utc timestamptz NOT NULL
);

CREATE TABLE subscription_events (
    id uuid PRIMARY KEY,
    subscription_id uuid NOT NULL REFERENCES subscriptions(id),
    provider_event_id text NOT NULL UNIQUE,
    event_type text NOT NULL,
    provider_created_at_utc timestamptz NULL,
    received_at_utc timestamptz NOT NULL,
    payload_sha256 text NOT NULL,
    processed_at_utc timestamptz NULL,
    processing_result text NULL
);

CREATE TABLE projects (
    id uuid PRIMARY KEY,
    owner_user_id uuid NOT NULL REFERENCES users(id),
    title text NOT NULL,
    state text NOT NULL,
    source_sha256 text NOT NULL,
    rights_attestation_version text NOT NULL,
    rights_attestation_text text NOT NULL,
    rights_attested_at_utc timestamptz NOT NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL
);

CREATE TABLE project_publications (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    state text NOT NULL,
    playable boolean NOT NULL,
    discoverable boolean NOT NULL,
    shareable boolean NOT NULL,
    revenue_eligible boolean NOT NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL
);

CREATE TABLE youtube_assets (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    publication_id uuid NOT NULL REFERENCES project_publications(id) ON DELETE CASCADE,
    youtube_video_id text NOT NULL UNIQUE,
    youtube_channel_id text NOT NULL,
    visibility text NOT NULL,
    uploaded_at_utc timestamptz NOT NULL,
    last_refreshed_at_utc timestamptz NOT NULL
);

CREATE TABLE youtube_status_events (
    id uuid PRIMARY KEY,
    youtube_asset_id uuid NOT NULL REFERENCES youtube_assets(id) ON DELETE CASCADE,
    status text NOT NULL,
    playable boolean NOT NULL,
    checked_at_utc timestamptz NOT NULL,
    response_sha256 text NOT NULL
);

CREATE TABLE project_contributors (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    user_id uuid NOT NULL REFERENCES users(id),
    role text NOT NULL,
    invitation_status text NOT NULL,
    proposed_split_basis_points integer NOT NULL CHECK (proposed_split_basis_points BETWEEN 0 AND 10000),
    invited_at_utc timestamptz NOT NULL,
    accepted_at_utc timestamptz NULL,
    UNIQUE(project_id, user_id)
);

CREATE TABLE contribution_events (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects(id),
    contributor_user_id uuid NOT NULL REFERENCES users(id),
    contribution_type text NOT NULL,
    status text NOT NULL,
    source_version integer NOT NULL,
    result_version integer NULL,
    evidence_json jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL,
    decided_at_utc timestamptz NULL
);

CREATE TABLE feedback_entries (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects(id),
    author_user_id uuid NOT NULL REFERENCES users(id),
    feedback_type text NOT NULL,
    body text NOT NULL,
    source_time_ms bigint NULL CHECK (source_time_ms IS NULL OR source_time_ms >= 0),
    ad_segment_id text NULL,
    qualification_status text NOT NULL CHECK (qualification_status IN ('PENDING', 'QUALIFIED', 'REJECTED', 'REVERSED')),
    reason_code text NULL,
    created_at_utc timestamptz NOT NULL,
    decided_at_utc timestamptz NULL
);

CREATE TABLE referral_links (
    id uuid PRIMARY KEY,
    owner_user_id uuid NOT NULL REFERENCES users(id),
    signed_code_hash text NOT NULL UNIQUE,
    destination_path text NOT NULL,
    created_at_utc timestamptz NOT NULL,
    disabled_at_utc timestamptz NULL
);

CREATE TABLE referral_visits (
    id uuid PRIMARY KEY,
    referral_link_id uuid NOT NULL REFERENCES referral_links(id),
    visitor_pseudonym_hash text NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    is_self_referral boolean NOT NULL,
    qualification_status text NOT NULL,
    reason_code text NULL
);

CREATE TABLE referral_relationships (
    id uuid PRIMARY KEY,
    referral_link_id uuid NOT NULL REFERENCES referral_links(id),
    referred_user_id uuid NOT NULL REFERENCES users(id),
    established_at_utc timestamptz NOT NULL,
    status text NOT NULL,
    UNIQUE(referred_user_id)
);

CREATE TABLE qualified_downstream_actions (
    id uuid PRIMARY KEY,
    referral_relationship_id uuid NOT NULL REFERENCES referral_relationships(id),
    action_type text NOT NULL,
    source_entity_id uuid NULL,
    qualification_status text NOT NULL,
    reason_code text NULL,
    occurred_at_utc timestamptz NOT NULL
);

CREATE TABLE playlists (
    id uuid PRIMARY KEY,
    owner_user_id uuid NULL REFERENCES users(id),
    title text NOT NULL,
    description text NOT NULL,
    is_public boolean NOT NULL,
    is_community_curated boolean NOT NULL,
    created_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL
);

CREATE TABLE playlist_items (
    playlist_id uuid NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
    project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    added_by_user_id uuid NULL REFERENCES users(id),
    added_at_utc timestamptz NOT NULL,
    PRIMARY KEY (playlist_id, project_id),
    UNIQUE (playlist_id, ordinal)
);

CREATE TABLE project_split_versions (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects(id),
    version integer NOT NULL CHECK (version > 0),
    effective_from_utc timestamptz NOT NULL,
    accepted_at_utc timestamptz NOT NULL,
    superseded_at_utc timestamptz NULL,
    UNIQUE(project_id, version)
);

CREATE TABLE split_participants (
    split_version_id uuid NOT NULL REFERENCES project_split_versions(id) ON DELETE CASCADE,
    user_id uuid NOT NULL REFERENCES users(id),
    basis_points integer NOT NULL CHECK (basis_points BETWEEN 0 AND 10000),
    accepted_at_utc timestamptz NOT NULL,
    PRIMARY KEY(split_version_id, user_id)
);

CREATE TABLE rights_interests (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects(id),
    claimant_user_id uuid NULL REFERENCES users(id),
    claimant_display text NOT NULL,
    interest_type text NOT NULL,
    status text NOT NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE TABLE rights_claims (
    id uuid PRIMARY KEY,
    rights_interest_id uuid NOT NULL REFERENCES rights_interests(id),
    evidence_json jsonb NOT NULL,
    status text NOT NULL,
    reserve_basis_points integer NOT NULL CHECK (reserve_basis_points BETWEEN 0 AND 10000),
    created_at_utc timestamptz NOT NULL,
    decided_at_utc timestamptz NULL
);

CREATE TABLE economic_policy_versions (
    version text PRIMARY KEY,
    community_basis_points integer NOT NULL CHECK (community_basis_points = 7000),
    adfi_basis_points integer NOT NULL CHECK (adfi_basis_points = 3000),
    subpool_weights_json jsonb NOT NULL,
    minimum_payout_usd numeric(18,2) NOT NULL CHECK (minimum_payout_usd >= 0),
    effective_from_utc timestamptz NOT NULL,
    retired_at_utc timestamptz NULL
);

INSERT INTO economic_policy_versions(version, community_basis_points, adfi_basis_points, subpool_weights_json, minimum_payout_usd, effective_from_utc)
VALUES ('2026-10-02.1', 7000, 3000, '{"referral":25,"contribution":35,"content":40}'::jsonb, 10.00, '2026-10-02T00:00:00Z');

CREATE TABLE revenue_periods (
    id uuid PRIMARY KEY,
    period_start_utc timestamptz NOT NULL,
    period_end_utc timestamptz NOT NULL,
    policy_version text NOT NULL REFERENCES economic_policy_versions(version),
    status text NOT NULL,
    net_subscription_revenue_usd numeric(18,2) NULL,
    community_allocation_usd numeric(18,2) NULL,
    adfi_allocation_usd numeric(18,2) NULL,
    closed_at_utc timestamptz NULL,
    CHECK (period_end_utc > period_start_utc),
    UNIQUE(period_start_utc, period_end_utc)
);

CREATE TABLE net_revenue_components (
    id uuid PRIMARY KEY,
    revenue_period_id uuid NOT NULL REFERENCES revenue_periods(id),
    component_type text NOT NULL,
    amount_usd numeric(18,2) NOT NULL CHECK (amount_usd >= 0),
    provider_reference text NULL,
    occurred_at_utc timestamptz NOT NULL
);

CREATE TABLE economic_events (
    id uuid PRIMARY KEY,
    member_user_id uuid NOT NULL REFERENCES users(id),
    project_id uuid NULL REFERENCES projects(id),
    referral_relationship_id uuid NULL REFERENCES referral_relationships(id),
    event_type text NOT NULL,
    qualification_status text NOT NULL CHECK (qualification_status IN ('PENDING', 'QUALIFIED', 'REJECTED', 'REVERSED')),
    reason_code text NOT NULL,
    weight numeric(18,6) NOT NULL CHECK (weight >= 0),
    policy_version text NOT NULL REFERENCES economic_policy_versions(version),
    split_version_id uuid NULL REFERENCES project_split_versions(id),
    occurred_at_utc timestamptz NOT NULL,
    decided_at_utc timestamptz NULL
);

CREATE TABLE member_earnings (
    id uuid PRIMARY KEY,
    revenue_period_id uuid NOT NULL REFERENCES revenue_periods(id),
    economic_event_id uuid NOT NULL REFERENCES economic_events(id),
    member_user_id uuid NOT NULL REFERENCES users(id),
    amount_usd numeric(18,2) NOT NULL CHECK (amount_usd >= 0),
    status text NOT NULL,
    explanation_json jsonb NOT NULL,
    created_at_utc timestamptz NOT NULL,
    UNIQUE(revenue_period_id, economic_event_id, member_user_id)
);

CREATE TABLE wallets (
    user_id uuid PRIMARY KEY REFERENCES users(id),
    pending_usd numeric(18,2) NOT NULL DEFAULT 0,
    available_usd numeric(18,2) NOT NULL DEFAULT 0,
    held_usd numeric(18,2) NOT NULL DEFAULT 0,
    lifetime_earnings_usd numeric(18,2) NOT NULL DEFAULT 0,
    version bigint NOT NULL DEFAULT 0
);

CREATE TABLE payout_batches (
    id uuid PRIMARY KEY,
    provider_batch_id text NOT NULL UNIQUE,
    status text NOT NULL,
    created_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz NULL
);

CREATE TABLE payout_requests (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id),
    payout_batch_id uuid NULL REFERENCES payout_batches(id),
    amount_usd numeric(18,2) NOT NULL CHECK (amount_usd > 0),
    paypal_destination_reference text NOT NULL,
    idempotency_key text NOT NULL UNIQUE,
    status text NOT NULL CHECK (status IN ('REQUESTED', 'QUEUED', 'SENT', 'SUCCEEDED', 'FAILED', 'REVERSED')),
    requested_at_utc timestamptz NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    provider_item_id text NULL UNIQUE,
    failure_code text NULL
);

CREATE TABLE wallet_entries (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id),
    member_earning_id uuid NULL REFERENCES member_earnings(id),
    payout_request_id uuid NULL REFERENCES payout_requests(id),
    entry_type text NOT NULL,
    amount_usd numeric(18,2) NOT NULL,
    explanation text NOT NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE TABLE moderation_reports (
    id uuid PRIMARY KEY,
    reporter_user_id uuid NULL REFERENCES users(id),
    project_id uuid NULL REFERENCES projects(id),
    feedback_entry_id uuid NULL REFERENCES feedback_entries(id),
    reason_code text NOT NULL,
    detail text NOT NULL,
    status text NOT NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE TABLE moderation_actions (
    id uuid PRIMARY KEY,
    report_id uuid NOT NULL REFERENCES moderation_reports(id),
    moderator_user_id uuid NOT NULL REFERENCES users(id),
    action_type text NOT NULL,
    reason_code text NOT NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE TABLE audit_events (
    id uuid PRIMARY KEY,
    actor_user_id uuid NULL REFERENCES users(id),
    event_type text NOT NULL,
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    correlation_id text NOT NULL,
    details_json jsonb NOT NULL
);

CREATE TABLE feature_configuration (
    key text PRIMARY KEY,
    value_json jsonb NOT NULL,
    version bigint NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    updated_by_user_id uuid NULL REFERENCES users(id)
);

CREATE INDEX ix_auth_sessions_active ON auth_sessions(token_sha256, expires_at_utc) WHERE revoked_at_utc IS NULL;
CREATE INDEX ix_subscriptions_user_status ON subscriptions(user_id, status);
CREATE INDEX ix_projects_owner_updated ON projects(owner_user_id, updated_at_utc DESC);
CREATE INDEX ix_publications_discover ON project_publications(discoverable, updated_at_utc DESC) WHERE discoverable;
CREATE INDEX ix_feedback_project_time ON feedback_entries(project_id, source_time_ms);
CREATE INDEX ix_referral_visits_link_time ON referral_visits(referral_link_id, occurred_at_utc);
CREATE INDEX ix_economic_events_period_source ON economic_events(occurred_at_utc, qualification_status, event_type);
CREATE INDEX ix_wallet_entries_user_time ON wallet_entries(user_id, created_at_utc DESC);
CREATE INDEX ix_audit_entity_time ON audit_events(entity_type, entity_id, occurred_at_utc DESC);
