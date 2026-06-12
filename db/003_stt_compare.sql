-- STT A/B observability: every compare run is persisted — audio reference, both
-- transcripts, timings, word-level diff, and the optional extraction comparison.
-- POC runs on synthetic dictation only; audio files live on disk (data/stt-compare/).

CREATE TABLE IF NOT EXISTS stt_compare_run (
  id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  layout_key          TEXT,
  layout_version      INT,
  audio_format        TEXT NOT NULL,
  audio_bytes         INT  NOT NULL,
  audio_seconds       REAL,
  audio_path          TEXT NOT NULL,
  note                TEXT,
  whisper_text        TEXT,
  whisper_ms          INT,
  whisper_error       TEXT,
  deepgram_text       TEXT,
  deepgram_ms         INT,
  deepgram_error      TEXT,
  deepgram_request_id TEXT,
  deepgram_model      TEXT,
  divergence          REAL,   -- word edit distance / max(words) — denormalized for the list view
  conflicts           INT,    -- extraction fields where the engines produced different values
  diff                JSONB,
  extraction          JSONB
);

CREATE INDEX IF NOT EXISTS ix_stt_compare_created ON stt_compare_run (created_at DESC);
