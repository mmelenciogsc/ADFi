INSERT INTO feature_configuration(key, value_json, version, updated_at_utc, updated_by_user_id)
VALUES ('YoutubeEphemeralAccessibilityCaptureEnabled', 'false'::jsonb, 1, now(), NULL)
ON CONFLICT (key) DO NOTHING;

INSERT INTO feature_configuration(key, value_json, version, updated_at_utc, updated_by_user_id)
VALUES ('ChatGptPlanUsageIntegrationEnabled', 'false'::jsonb, 1, now(), NULL)
ON CONFLICT (key) DO NOTHING;

CREATE TABLE project_scene_evidence (
    id uuid PRIMARY KEY,
    project_id uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    publication_id uuid NOT NULL REFERENCES project_publications(id) ON DELETE CASCADE,
    scene_id text NOT NULL,
    start_time_ms bigint NOT NULL CHECK (start_time_ms >= 0),
    end_time_ms bigint NOT NULL CHECK (end_time_ms > start_time_ms),
    objective_visual_summary text NULL,
    established_entities_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    ocr_context_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    dialogue_context_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    ad_text text NULL,
    evidence_confidence numeric(5,4) NOT NULL CHECK (evidence_confidence BETWEEN 0 AND 1),
    provenance_version text NOT NULL,
    created_at_utc timestamptz NOT NULL,
    UNIQUE(publication_id, scene_id)
);

CREATE INDEX ix_project_scene_evidence_moment
    ON project_scene_evidence(publication_id, start_time_ms, end_time_ms);

CREATE TABLE viewer_telemetry_events (
    id uuid PRIMARY KEY,
    user_id uuid NULL REFERENCES users(id) ON DELETE SET NULL,
    event_type text NOT NULL CHECK (event_type IN (
        'viewer_session_started', 'ask_invoked', 'answer_succeeded', 'resume_invoked')),
    question_category text NULL,
    provider_backend text NULL,
    source_class text NOT NULL,
    latency_bucket text NULL,
    occurred_at_utc timestamptz NOT NULL,
    CHECK (question_category IS NULL OR length(question_category) <= 64),
    CHECK (provider_backend IS NULL OR length(provider_backend) <= 64),
    CHECK (length(source_class) <= 32)
);

COMMENT ON TABLE viewer_telemetry_events IS
    'Coarse ADFi-native viewer telemetry only. Question text, screenshots, media paths and YouTube engagement are prohibited.';
