# SynForm POC — Data-Driven, Multi-Modal Clinical Form Engine

**Product context:** Synexar AIMS component. Reusable Angular control (`<syn-form>`) for schema-driven clinical data entry with keyboard, voice, photo, and agent/CLI input modalities. Targets web (desktop) and iPad 11" (Capacitor) from a single codebase.

**Spec version:** 1.0 — POC scope only. Build exactly what is specified; do not add features, abstractions, or configuration beyond this document.

---

## 1. Goals & Non-Goals

### Goals
1. **Data-driven fields:** Field metadata (labels, types, options, validation, dependencies) lives in the database as versioned layout JSON. Changing a label, adding a dropdown option, or toggling required happens in a Maintainer page — no deploy.
2. **Design-time page composition:** Field *positioning* on the page is design-time (host page decides layout slots). Only field *metadata* is runtime-data-driven.
3. **One reusable component:** `<syn-form>` importable into any AIMS page. Renders fields from a layout, exposes a Reactive FormGroup, generic save, and a `populate()` API.
4. **Multi-modal input through one seam:** Voice, photo, and CLI/agent all produce a partial-values object that flows through the same `populate()` + validation path. One `/extract` backend endpoint serves all modalities.
5. **AI-friendly by construction:** Every published layout is exposable as JSON Schema; the form is simultaneously a UI and a machine endpoint.
6. **iPad-first ergonomics:** Same component renders correctly on desktop web and iPad 11" (landscape primary), with touch-appropriate control variants. Prove native mic access through Capacitor.
7. **Maintainer with instant feedback:** Admin page to edit field metadata with a live preview of the rendered form side-by-side.

### Non-Goals (POC)
- No drag-drop form designer. No dynamic page layout engine.
- No multi-tenant auth/RBAC (single user, no login).
- No HL7/FHIR integration in this POC.
- No offline sync.
- No production deployment pipeline; runs locally (dev server + local API + local Postgres + Ollama).

---

## 2. Tech Stack

| Layer | Choice | Notes |
|---|---|---|
| Frontend | Angular 18+ (standalone components, signals OK), Reactive Forms, Angular CDK (overlay) | No React. No NgRx — keep state in services/signals. |
| Mobile shell | Capacitor 6 (iOS) | iPad 11" target. Dev deploy via Xcode. |
| Backend | .NET 8 Web API (C#) | Matches existing AIMS stack. |
| Database | PostgreSQL 16, JSONB columns | |
| LLM (local) | Ollama running Qwen 2.5 7B Instruct | Structured output via JSON Schema / format parameter. |
| LLM (cloud fallback) | OpenAI gpt-4o-mini (text), gpt-4o (vision/photo) | Provider-switchable by config. |
| STT (POC) | Browser Web Speech API is NOT acceptable. Use Deepgram streaming (nova-2 or nova-3-medical) via WebSocket, key in config. | Local Whisper is out of POC scope; design the audio service so it can be swapped later. |
| Realtime (optional) | Not required for POC. `populate()` is called directly from the extraction response. | |

---

## 3. Data Model (PostgreSQL)

```sql
-- Versioned layouts. Published versions are IMMUTABLE.
CREATE TABLE form_layout (
  id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  layout_key   TEXT NOT NULL,            -- stable identifier, e.g. 'pre-anesthesia-assessment'
  version      INT  NOT NULL,
  status       TEXT NOT NULL CHECK (status IN ('draft','published')),
  json         JSONB NOT NULL,           -- the layout schema (section 4)
  created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  published_at TIMESTAMPTZ,
  UNIQUE (layout_key, version)
);

-- Shared lookup lists with parent-key support for cascading.
CREATE TABLE lookup_item (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  lookup_key  TEXT NOT NULL,             -- e.g. 'procedures'
  value       TEXT NOT NULL,             -- stored value
  label       TEXT NOT NULL,             -- display label
  parent_key  TEXT NULL,                 -- for cascading: matches parent field's selected value
  sort_order  INT NOT NULL DEFAULT 0,
  active      BOOLEAN NOT NULL DEFAULT true
);
CREATE INDEX ix_lookup ON lookup_item (lookup_key, parent_key);

-- Saved form records. Always stamped with layout version.
CREATE TABLE form_record (
  id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  layout_key     TEXT NOT NULL,
  layout_version INT  NOT NULL,
  values         JSONB NOT NULL,         -- { fieldName: value }
  provenance     JSONB NOT NULL,         -- { fieldName: {source, confidence, ts} }
  created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

**Versioning rules (enforced in API):**
- Editing a published layout creates a new draft version (`version + 1`).
- Publish validates the layout (section 4.6) and sets status=published. Published rows are never updated.
- `form_record` saves always validate against and stamp the exact published version used.

---

## 4. Layout JSON Schema (single source of truth)

```jsonc
{
  "layoutKey": "pre-anesthesia-assessment",
  "version": 3,
  "title": "Pre-Anesthesia Assessment",
  "fields": [
    {
      "name": "asaClass",                  // unique, camelCase, stable — never renamed after publish
      "label": "ASA Classification",       // editable in Maintainer
      "controlType": "radio",              // see 4.1
      "dataType": "string",                // string|number|boolean|date|time|string[]|bpPair
      "required": true,                     // OR use requiredWhen (4.3)
      "options": { "inline": [              // inline OR lookup (4.2) — exactly one
        {"value":"1","label":"ASA I"},
        {"value":"2","label":"ASA II"},
        {"value":"3","label":"ASA III"},
        {"value":"4","label":"ASA IV"}
      ]},
      "group": "assessment",               // visual grouping hint for host page slots
      "order": 10,
      "unit": null,                         // e.g. "kg", "cm" for numbers
      "min": null, "max": null,             // numeric/date bounds
      "pattern": null,                      // regex for text
      "aliases": ["ASA", "ASA class", "physical status"],  // voice/extraction trigger phrases
      "visibleWhen": null,                  // condition (4.3)
      "enabledWhen": null,
      "requiredWhen": null,
      "computedFrom": null,                 // derived fields (4.4)
      "filterBy": null                      // cascading lookups (4.2)
    }
  ],
  "crossFieldRules": [                      // evaluated on save (4.5)
    {
      "id": "npo-before-procedure",
      "message": "NPO time must be before procedure time",
      "condition": { "field": "npoTime", "op": "lt", "valueFromField": "procedureTime" }
    }
  ]
}
```

### 4.1 Control type vocabulary (closed set — exactly these 11)
| controlType | dataType | Desktop render | Touch render (pointer: coarse) |
|---|---|---|---|
| `text` | string | input | input, 48px min height |
| `textarea` | string | textarea | textarea |
| `number` | number | input + unit suffix | numeric stepper (+/- buttons) + unit |
| `dropdown` | string | select / autocomplete if >12 options | **segmented buttons if ≤5 options**, else full-screen-ish sheet picker |
| `multiselect` | string[] | multi-select listbox | tappable chips |
| `radio` | string | radio group | segmented buttons |
| `checkbox` | boolean | checkbox | switch-style toggle |
| `checkboxGroup` | string[] | checkbox list | tappable chips |
| `date` | date | date input | native date picker |
| `time` | time | time input | native time picker |
| `bpPair` | bpPair `{sys, dia}` | two linked number inputs "120 / 80" | two steppers |

The render-variant decision is made by the **renderer** via `(pointer: coarse)` media query + container width. The layout JSON never encodes surface-specific information.

### 4.2 Options & cascading lookups
- `options` is exactly one of:
  - `{ "inline": [ {value,label}, ... ] }` — for small static lists (ASA, Mallampati).
  - `{ "lookupKey": "procedures" }` — resolved from `lookup_item` at render time.
- **Cascading:** child field sets `"filterBy": "procedureCategory"`. The component watches the parent control; on change it refilters lookup items where `parent_key == parent value`. **If the child's current value is no longer in the filtered set, clear it and mark the field as needs-review (amber).** Never silently keep stale values.

### 4.3 Condition syntax (used by visibleWhen / enabledWhen / requiredWhen)
```jsonc
// Single condition
{ "field": "smoker", "op": "eq", "value": "yes" }
// Composition (one level of and/or is sufficient for POC)
{ "and": [ {...}, {...} ] }   |   { "or": [ {...}, {...} ] }
```
Operator set is **closed**: `eq, neq, in, gt, lt, gte, lte, notEmpty`. No expression language. No eval.

Behavioral rules:
- A field hidden by `visibleWhen` contributes no value on save and **rejects `populate()`** (extracted values for hidden fields are dropped and reported back as skipped).
- `requiredWhen` overrides `required` when present.

### 4.4 Derived fields
`"computedFrom": { "fn": "bmi", "inputs": ["heightCm", "weightKg"] }` — read-only, recalculated on input change. POC implements exactly one function: `bmi`. The function registry is a TypeScript map; adding functions is a code change (acceptable — these are rare and must be tested).

### 4.5 Validation tiers (all enforced server-side on save; client mirrors for UX)
1. Per-field: required/requiredWhen, min/max (+units), pattern, date bounds, **option membership** (value must exist in resolved options — server checks against lookup table or inline list).
2. Cross-field: `crossFieldRules` array, same condition syntax with `valueFromField` support.
3. Type coercion: server rejects type mismatches; no silent coercion on save (extraction layer does normalization *before* values reach the form).

### 4.6 Publish-time validation (Maintainer "Publish" action — fail fast at design time)
- All `field.name` unique, camelCase.
- All `filterBy`, condition `field`, `computedFrom.inputs`, `crossFieldRules` references resolve to existing fields.
- Dependency graph (filterBy + conditions + computedFrom) is a **DAG** — cycles fail the publish with a clear error naming the cycle.
- Every `lookupKey` referenced exists in `lookup_item`.
- Each field has exactly one of `options.inline` / `options.lookupKey` (or neither, for non-option controls).

---

## 5. The `<syn-form>` Angular Component

### Public API
```ts
@Component({ selector: 'syn-form', standalone: true })
export class SynFormComponent {
  // Inputs
  layoutKey   = input.required<string>();
  version     = input<number | 'latestPublished'>('latestPublished');
  density     = input<'comfortable' | 'compact'>('comfortable'); // comfortable = touch default

  // Outputs
  saved       = output<{ recordId: string }>();
  dirtyChange = output<boolean>();

  // Public methods
  populate(values: PartialValues, source: 'voice'|'photo'|'agent'|'manual', confidences?: Record<string, number>): PopulateResult;
  save(): Promise<SaveResult>;        // posts {layoutKey, layoutVersion, values, provenance}
  getFormGroup(): FormGroup;          // escape hatch for host pages
  reset(): void;
}

interface PopulateResult {
  applied: string[];      // fields set
  skipped: { field: string; reason: 'hidden'|'unknownField'|'invalidOption'|'readOnly' }[];
}
```

### Rendering rules
- Component fetches layout JSON once, builds the FormGroup (controls, validators, disabled states) from it.
- Fields render into **named slot groups** (`group` property). Host page positions groups via content projection:
  ```html
  <syn-form layoutKey="pre-anesthesia-assessment">
    <div class="left-col"  synFormGroup="patient"></div>
    <div class="left-col"  synFormGroup="assessment"></div>
    <div class="right-col" synFormGroup="airway"></div>
  </syn-form>
  ```
  (Implementation detail is the builder's choice — directive-marked containers or named ng-content — but group→position mapping is host-page markup, i.e., design-time, per the architecture decision.)
- **Container queries, not viewport queries:** the component adapts to its own width. Breakpoints: ≥900px container → two-column field grid within a group; 600–900px → single column; <600px → compact stacking.
- **Touch density:** at `density='comfortable'`, all interactive controls ≥48px height, ≥44px touch targets, generous spacing. Control variant substitution per the table in 4.1, decided by `(pointer: coarse)`.
- **Per-field state visuals:**
  - AI-populated + confidence ≥ 0.85 → brief green flash, then normal.
  - AI-populated + confidence < 0.85 → **amber border + amber dot, persists until the user focuses/edits/confirms the field.**
  - Cleared-by-cascade → amber needs-review state.
  - Validation error → red border + message below.
- **Provenance:** every value set carries `{source, confidence?, ts}` into a provenance map, saved alongside values. Manual edits overwrite provenance with `{source:'manual'}`.
- **Keyboard avoidance (Capacitor):** subscribe to Capacitor Keyboard plugin show/hide events; scroll the focused control into view above the keyboard. Implement once inside syn-form.
- **No `if (isIpad)` branching in component logic.** Surface differences live only in CSS (container queries, pointer media queries) and the renderer's control-variant selection.

---

## 6. Backend API (.NET 8)

```
GET  /api/layouts/{layoutKey}                      → latest published layout JSON
GET  /api/layouts/{layoutKey}/versions/{v}         → specific version
GET  /api/layouts/{layoutKey}/schema               → JSON Schema (draft 2020-12) generated from the layout (the machine/agent surface)
POST /api/layouts                                  → create/update draft (Maintainer)
POST /api/layouts/{layoutKey}/publish              → publish-time validation (4.6), then immutable publish

GET  /api/lookups/{lookupKey}?parent={value}       → filtered lookup items

POST /api/records                                  → save form record
     body: { layoutKey, layoutVersion, values, provenance }
     → full server-side validation (4.5) against that exact version; 422 with per-field errors on failure

POST /api/extract                                  → multi-modal extraction (section 7)
     body: { layoutKey, inputType: 'text'|'transcript'|'image', text?, imageBase64? }
     → { values: {...}, confidences: {field: 0..1}, unmatchedText?: string }
```

JSON Schema generation (`/schema`): map dataTypes to JSON Schema types, options to `enum`, required/requiredWhen→`required` (static required only; conditional noted in `description`), units/bounds to `minimum/maximum` + `description`. This endpoint is what the CLI modality consumes.

---

## 7. Extraction Pipeline (one seam, all modalities)

### Layered design — cheapest layer first
```
input → [Layer 1: Grammar/Regex resolver] → unresolved remainder → [Layer 2: LLM structured extraction] → [Normalizer] → values + confidences
```

**Layer 1 — Deterministic resolver (no LLM):**
- Compiled **from the layout** at request time (cache per layout version): field `label` + `aliases` become trigger phrases; enum option labels/values become match vocabulary; number+unit patterns and bpPair patterns ("120 over 80", "one twenty over eighty" → numeric normalization) are regex.
- Handles field-targeted utterances: `"ASA three"`, `"weight 82 kilos"`, `"BP 110 over 70"`, `"Mallampati class 2"`.
- Matches get confidence 0.95+. Fuzzy option matching: Levenshtein distance ≤ 2 (normalized) against option labels; below threshold → pass to Layer 2.
- Spelled-out numbers: implement a small word-to-number converter (zero–two hundred is sufficient for vitals).

**Routing rule:** if Layer 1 resolves ≥1 field AND consumes ≥70% of the input tokens → skip the LLM. Otherwise send the full input + Layer 1 partial results to Layer 2.

**Layer 2 — LLM structured extraction (provider-agnostic):**
```csharp
public interface IExtractionProvider {
  Task<ExtractionResult> ExtractAsync(LayoutSchema layout, ExtractionInput input);
}
// Implementations: OllamaProvider (Qwen 2.5 7B, format=json_schema), OpenAiProvider (gpt-4o-mini text / gpt-4o vision)
// Selected via appsettings: "Extraction:Provider": "ollama" | "openai"
```
- System prompt is generated from the layout: field names, types, allowed enum values, units, and explicit instructions: *output only fields explicitly stated; never infer; handle negations ("no smoking history" → smoker="no"); omit uncertain fields rather than guessing.*
- Output constrained to the layout-derived JSON Schema (Ollama `format`, OpenAI structured outputs).
- LLM confidence: model self-reports per-field confidence in the schema (`{value, confidence}` pairs); clamp LLM confidences to max 0.9.

**Normalizer (after both layers, deterministic):**
- Option membership fuzzy-snap (Levenshtein) or reject.
- Unit coercion to the field's declared unit (lbs→kg, in→cm).
- Date/time parsing to ISO.
- Anything failing normalization is dropped and reported in `skipped`.

**Photo modality:** `inputType:'image'` always routes to the vision-capable provider (gpt-4o for POC; note in code where a local VLM would slot in). Same output contract.

**CLI modality:** no extraction at all — agent GETs `/schema`, POSTs `/records` directly. Provide a sample script (~30 lines, Node or Python, builder's choice) that fetches the schema, prints it, fills values, saves, and prints the record id. This proves the form is a machine endpoint.

---

## 8. Voice Panel (component: `<syn-voice-panel>`)

- Floating panel via CDK overlay; desktop: bottom-right; iPad/touch: docked bottom-center, above home indicator, thumb-reachable. Contains: mic toggle, live transcript line, processing spinner, last-result summary ("4 fields updated, 1 needs review").
- Audio path behind an interface:
  ```ts
  interface AudioSource { start(): Observable<AudioChunkOrTranscript>; stop(): void; }
  // POC implementation: WebAudioSource using getUserMedia → Deepgram WebSocket streaming.
  // Stub a NativeAudioSource class (Capacitor plugin path, AVAudioEngine 16kHz PCM) — interface only, not implemented in POC.
  ```
  Resolve implementation via `Capacitor.isNativePlatform()` — POC resolves both to WebAudioSource; the seam is what we're proving.
- **Utterance endpointing:** use Deepgram's endpointing/`speech_final`; on utterance end, send the final transcript to `/extract`, then call `synForm.populate(values, 'voice', confidences)`.
- Correction commands are in scope: Layer 1 must recognize `"change {field} to {value}"` and `"clear {field}"` patterns.
- iOS requirement: add `NSMicrophoneUsageDescription` to Info.plist (hard crash without it).

---

## 9. Maintainer Page (instant feedback)

Route: `/maintainer/{layoutKey}`. Two-pane layout:
- **Left pane — field grid (the DataViewer):** one row per field: name (read-only once published), label, controlType, required, options editor (inline list editor or lookupKey picker), order (drag or up/down), aliases (tag input), visibleWhen/requiredWhen (simple condition builder: field dropdown + op dropdown + value input; one-level and/or).
- **Right pane — live preview:** an actual `<syn-form>` instance rendering the **draft** layout, re-rendered on every edit (debounced ~300ms). This is the instant feedback. Preview banner: "DRAFT v4 — unpublished".
- Actions: **Save Draft** (persists draft JSON), **Publish** (runs 4.6 validation; on failure show errors inline per field; on success, version becomes immutable and live forms pick it up on next load), **Preview on touch** (toggle that forces `density='comfortable'` + coarse-pointer rendering in the preview so iPad variants can be checked from the desktop).
- Also include a read-only **"View JSON Schema"** drawer showing the generated `/schema` output — demonstrates the AI-friendly surface to stakeholders.

Lookup management: a second simple grid at `/maintainer/lookups` — CRUD on `lookup_item` rows (lookup_key, value, label, parent_key, order, active).

---

## 10. POC Form — Pre-Anesthesia Assessment (the one complicated form)

~18 fields exercising every control type, one cascade, one conditional-required, one derived field, one cross-field rule:

| # | name | control | Notes |
|---|---|---|---|
| 1 | patientName | text | required |
| 2 | dob | date | required, max=today |
| 3 | heightCm | number (cm) | min 30 max 250 |
| 4 | weightKg | number (kg) | min 1 max 400 |
| 5 | bmi | number, **computedFrom** heightCm+weightKg | read-only |
| 6 | asaClass | radio (inline I–IV) | required |
| 7 | mallampati | dropdown (inline I–IV) | |
| 8 | bp | bpPair | sys 60–260, dia 30–160 |
| 9 | heartRate | number (bpm) | 20–250 |
| 10 | allergies | multiselect (lookupKey: allergies) | include NKDA option |
| 11 | allergyOther | text | **visibleWhen** allergies contains "other" |
| 12 | npoTime | time | required; cross-field rule vs procedureTime |
| 13 | comorbidities | checkboxGroup (lookupKey: comorbidities) | |
| 14 | smoker | radio yes/no/former | |
| 15 | packYears | number | **visibleWhen** smoker in [yes, former]; **requiredWhen** smoker eq yes |
| 16 | procedureCategory | dropdown (lookupKey: procedureCategories) | required; cascade parent |
| 17 | procedure | dropdown (lookupKey: procedures, **filterBy** procedureCategory) | required |
| 18 | procedureTime | time | required |
| 19 | airwayNotes | textarea | |
| 20 | consentVerified | checkbox | required (must be true) |

Seed data: 4 procedure categories × 5–8 procedures each, ~10 allergies, ~10 comorbidities. Field aliases populated thoughtfully (e.g., bp aliases: "blood pressure", "BP", "pressure").

---

## 11. Build Sequence (do in this order; each step is demoable)

1. **DB + API foundation:** tables, seed layout + lookups, layout GET endpoints, generic record save with full server validation. *(Demo: curl save with valid/invalid payloads.)*
2. **`<syn-form>` keyboard-only:** rendering all 11 control types, container queries, validation UX, cascade behavior, derived BMI, conditional visibility/required, save. *(Demo: full form on desktop browser.)*
3. **iPad pass:** touch variants (segmented buttons, steppers, chips, native pickers), density tokens, test in iPad Safari at 1194×834 landscape. Then Capacitor shell via Xcode (free dev account is fine), keyboard avoidance, Info.plist mic entry. *(Demo: same form on the physical iPad in the Capacitor app.)*
4. **Maintainer:** field grid + live draft preview + publish validation + lookup CRUD + JSON Schema drawer. *(Demo: rename a label, add a dropdown option, publish, reload form — no deploy.)*
5. **/extract + populate():** Layer 1 resolver, Ollama provider, OpenAI provider, normalizer, confidence highlighting, provenance. *(Demo: POST a transcript string, watch fields fill with amber/green states.)*
6. **Voice panel:** Deepgram streaming, endpointing, correction commands. *(Demo: dictate the form on the iPad.)*
7. **Photo:** image upload → gpt-4o vision → populate. *(Demo: photo of a handwritten/printed pre-op sheet.)*
8. **CLI script:** fetch schema → save record. *(Demo: agent fills the form with zero UI.)*

## 12. Acceptance Criteria

- [ ] Label/option/required changes via Maintainer reflect in the form on reload with no code change or deploy.
- [ ] Published layouts are immutable; records stamp layout version; saves validate server-side against that version (422 with per-field errors).
- [ ] Publish rejects: dangling references, dependency cycles, missing lookups, duplicate names — with clear messages.
- [ ] Cascade: changing procedureCategory refilters procedure; stale procedure value is cleared + amber-flagged.
- [ ] Hidden-field values from `populate()` are skipped and reported; all populated values pass through normal validation.
- [ ] `"BP one twenty over eighty"` resolves via Layer 1 (no LLM call — verifiable in logs). A multi-field narrative utterance resolves via Layer 2 on **Ollama locally**; switching provider to OpenAI is a config change only.
- [ ] Confidence < 0.85 renders amber until user touch; provenance saved per field.
- [ ] Form is fully usable on iPad 11" landscape in the Capacitor shell: 48px touch targets, segmented buttons for short option lists, native date/time pickers, keyboard never obscures the focused field, mic permission prompt appears correctly.
- [ ] Voice: dictating a complete assessment populates ≥80% of stated fields correctly end-to-end; "change weight to 85" corrects a field.
- [ ] Photo of a filled paper form populates matching fields with confidences.
- [ ] CLI script fills and saves a record using only `/schema` + `/records`.
- [ ] Zero `isNative`/platform conditionals in syn-form component logic (CSS and renderer variants only); grep-verifiable.

## 13. Explicit Guardrails for the Implementing Agent

- Do not introduce a dynamic page-layout/designer system. Page composition is host-page markup.
- Do not expand the control type vocabulary or condition operator set.
- Do not add state-management libraries, micro-frontends, or speculative abstractions.
- Do not skip server-side validation "because the client validates."
- Keep the extraction prompt generated from the layout — no hand-written per-form prompts.
- All secrets (Deepgram, OpenAI) in environment config, never committed.
- Synthetic/test patient data only. No real PHI anywhere in the POC.
