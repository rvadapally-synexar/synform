# SynForm — Product Description

Marketing/positioning copy for the website, decks, and proposals. Three lengths — keep claims
in sync with what's actually built (see README for the technical inventory).

---

## Tagline

> **SynForm — clinical forms that build themselves around your data, and fill themselves from voice, vision, or AI agents.**

---

## Short (card / portfolio tile)

> **SynForm** is a data-driven clinical form engine. Form fields, validation rules, and
> vocabularies live as versioned data — not code — so clinical teams change forms in minutes
> with zero deploys. Every form is simultaneously a touch-ready UI and a machine endpoint:
> clinicians dictate it, photograph a paper sheet into it, or let an AI agent fill it through
> its auto-generated schema. Every AI-entered value carries provenance and a confidence state,
> and nothing reaches the record without server-side clinical validation.

---

## Full (project page)

### SynForm — Data-Driven, AI-Native Clinical Forms

Clinical documentation shouldn't require a software release. SynForm treats forms as
**data, not code**: every field, option list, validation rule, and dependency lives as
versioned layout JSON. Need a new dropdown option, a renamed label, or a conditional field
before tomorrow's cases? Edit it in the Maintainer studio with a live preview, publish when
*you* decide, and every form picks it up on next load — no deploy, no downtime. Published
versions are immutable, and every saved record is stamped with the exact version it was
validated against: a built-in audit trail.

**One form, four ways in.** SynForm is built AI-native from the ground up, with every input
modality flowing through a single validated pipeline:

- ⌨️ **Touch & keyboard** — one responsive component that adapts from desktop to iPad, with
  48px touch targets, segmented controls, and native pickers.
- 🎙️ **Voice** — dictate naturally ("BP one twenty over eighty, ASA three, allergic to
  penicillin and latex") and watch fields fill. A deterministic clinical-grammar layer
  resolves vitals instantly without an LLM; narrative speech routes to a language model.
  Speech recognition is vocabulary-biased by the form itself — your field names become the
  engine's dictionary.
- 📷 **Vision** — photograph a handwritten pre-op sheet and the fields populate from the image.
- 🤖 **Agents & CLI** — every published form exposes itself as a JSON Schema endpoint. Any AI
  agent or script can discover the form's shape, fill it, and save a record with zero UI.

**Trust is designed in.** AI-populated fields are visibly marked: high-confidence values
flash green, uncertain ones hold an amber "needs review" state until a clinician confirms
them. Every value carries provenance — who or what entered it, with what confidence, and
when. And the server trusts no one: every save is re-validated against the clinical rules,
whatever the source.

**Future-proof by architecture.** Speech, LLM, and storage providers sit behind clean seams —
today Azure-hosted Whisper and Claude under our Microsoft BAA umbrella, tomorrow whatever
wins on accuracy and cost, swapped in config, not in code. Offline-ready foundations
(client-generated record IDs, idempotent saves) mean field connectivity loss won't mean
data loss.

Built on Angular, .NET, and PostgreSQL. Designed for healthcare from day one: HIPAA-aware
logging, BAA-aligned AI routing, and synthetic-data development discipline.

---

## Usage notes

- Prefer "four ways in" (keyboard counts); drop the keyboard bullet if the rhythm of exactly
  three (voice, vision, agents) reads better in a given placement.
- Say "HIPAA-aware" / "BAA-aligned", **never** "HIPAA-compliant" — compliance is a property
  of a deployment, not a component.
- Demo anecdote that lands well: Whisper once hallucinated "For more information visit
  www…" onto trailing silence — and the validation pipeline ignored it. *Our validation
  layer is why an AI hallucination can't reach a patient record.*
