ALTER TABLE project_publications
    ADD COLUMN accessibility_summary text NOT NULL DEFAULT '',
    ADD COLUMN attribution text NOT NULL DEFAULT '';

CREATE INDEX ix_project_publications_discoverable
    ON project_publications(updated_at_utc DESC, project_id)
    WHERE discoverable = true AND playable = true;
