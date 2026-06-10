-- SynForm POC schema. Applied idempotently by the API migration runner at startup.

CREATE TABLE IF NOT EXISTS schema_migration (
  filename   TEXT PRIMARY KEY,
  applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Versioned layouts. Published versions are IMMUTABLE.
CREATE TABLE IF NOT EXISTS form_layout (
  id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  layout_key   TEXT NOT NULL,
  version      INT  NOT NULL,
  status       TEXT NOT NULL CHECK (status IN ('draft','published')),
  json         JSONB NOT NULL,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),  -- optimistic concurrency for drafts
  published_at TIMESTAMPTZ,
  UNIQUE (layout_key, version)
);

-- At most one draft per layout_key.
CREATE UNIQUE INDEX IF NOT EXISTS ux_form_layout_one_draft
  ON form_layout (layout_key) WHERE status = 'draft';

-- Shared lookup lists with parent-key support for cascading.
-- Items are never hard-deleted once referenced; deactivate instead.
CREATE TABLE IF NOT EXISTS lookup_item (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  lookup_key  TEXT NOT NULL,
  value       TEXT NOT NULL,
  label       TEXT NOT NULL,
  parent_key  TEXT NULL,
  sort_order  INT NOT NULL DEFAULT 0,
  active      BOOLEAN NOT NULL DEFAULT true,
  UNIQUE (lookup_key, value)
);
CREATE INDEX IF NOT EXISTS ix_lookup ON lookup_item (lookup_key, parent_key);

-- Saved form records. Always stamped with the layout version they were
-- validated against. Client supplies the id (idempotent upsert: offline-ready seam).
CREATE TABLE IF NOT EXISTS form_record (
  id             UUID PRIMARY KEY,
  layout_key     TEXT NOT NULL,
  layout_version INT  NOT NULL,
  field_values   JSONB NOT NULL,   -- { fieldName: value }  ("values" is reserved in PG)
  provenance     JSONB NOT NULL,   -- { fieldName: {source, confidence?, ts} }
  context        JSONB NULL,       -- host-supplied linkage, e.g. {encounterId}
  created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_form_record_layout ON form_record (layout_key, created_at DESC);
