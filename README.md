# SynForm POC — Data-Driven, Multi-Modal Clinical Form Engine

Reusable Angular `<syn-form>` component driven by versioned layout JSON in PostgreSQL,
with keyboard / voice / photo / CLI-agent input through one `populate()` seam, plus a
Maintainer UI to create, edit, and explicitly publish layouts. Spec: `synform-poc-spec.md`
(implemented with the v1.1 adversarial-review fixes — see *Spec deltas* below).

## Stack

| Layer | Tech |
|---|---|
| Frontend | Angular 18 workspace — `projects/syn-form` (library) + `projects/demo` (host app + Maintainer) |
| Backend | .NET 8 minimal API (`server/SynForm.Api`), Dapper + Npgsql |
| Database | PostgreSQL on **localhost:5436**, database `synform_poc` (user `synform_user`) |
| LLM | Ollama (qwen2.5:7b-instruct) or OpenAI — switchable via `Extraction:Provider` |
| STT | Deepgram streaming via short-lived token proxy (`POST /api/stt/token`) — key never reaches the browser |

## Run it

```powershell
# 1. API (applies db/*.sql migrations + seed automatically at startup)
cd server/SynForm.Api
dotnet run                     # → http://localhost:5266

# 2. Client
cd client
npm install
npx ng serve                   # → http://localhost:4200

# 3. CLI agent modality (zero UI)
node cli/fill-form.mjs         # GET /schema → POST /records
```

The database role/db are created once with:
```sql
CREATE ROLE synform_user LOGIN PASSWORD 'synform_dev_2026';
CREATE DATABASE synform_poc OWNER synform_user;
```

### Optional service keys (`server/SynForm.Api/appsettings.json`)
- `Extraction:Provider` — `ollama` (default, needs Ollama running) or `openai`
- `Extraction:OpenAi:ApiKey` — enables OpenAI text + **photo (gpt-4o vision)** extraction
- `Deepgram:ApiKey` — enables the live voice panel (mic → streaming STT → extract → populate)

Without keys, everything else works — including **Layer-1 extraction** (deterministic,
no LLM): try the *Agent/transcript simulator* on the form page with
`"BP one twenty over eighty, weight 82 kilos, ASA three"`.

## Test on an iPad (LAN, no Mac needed)

```powershell
# on the Windows dev box — one-time, elevated PowerShell:
New-NetFirewallRule -DisplayName "SynForm POC dev (4200,5266)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 4200,5266 -Profile Private,Domain

cd server/SynForm.Api ; dotnet run          # API binds 0.0.0.0:5266
cd client ; npx ng serve --host 0.0.0.0     # app binds 0.0.0.0:4200
```

Then on the iPad (same Wi-Fi): open **http://&lt;pc-ip&gt;:4200** in Safari.
The client auto-targets the API on the same host (`http://<hostname>:5266`).
Tip: Share → *Add to Home Screen* gives a chrome-less, app-like full screen.
Caveat: iPad Safari only allows microphone access on secure origins, so the live
voice panel needs HTTPS (e.g. an ngrok/Cloudflare tunnel) — the transcript
simulator and everything else works over plain HTTP.

## Run on a Mac

```bash
git clone https://github.com/rvadapally-synexar/synform.git && cd synform
# needs: .NET 8 SDK, Node 20+, PostgreSQL (any port — edit ConnectionStrings in
# server/SynForm.Api/appsettings.json), then create the role/db from the snippet above.
(cd server/SynForm.Api && dotnet run) &
(cd client && npm install && npx ng serve)
# Capacitor shell (the part that needs Xcode):
#   cd client && npm i @capacitor/core @capacitor/cli @capacitor/ios
#   npx ng build demo && npx cap init synform com.synexar.synform --web-dir dist/demo/browser
#   npx cap add ios && npx cap open ios   → add NSMicrophoneUsageDescription to Info.plist, run on iPad
```

## Pages

- `/` — Pre-Anesthesia Assessment host page (slot-group composition, voice panel, simulator)
- `/maintainer` — layout list: create / clone / edit
- `/maintainer/pre-anesthesia-assessment` — field grid + **live draft preview** + explicit publish + JSON Schema drawer + touch-preview toggle
- `/maintainer/lookups` — lookup CRUD (deactivate-only deletes)
- `/records` — saved records with values + per-field provenance

## Architecture notes

- **Versioning is explicit.** Editing never touches a published layout; *Create Draft vN+1*
  is a user action, publish runs §4.6 validation (dangling refs, dependency cycles named in
  the error, alias collisions, cross-version dataType stability), then the version is immutable.
  One draft per layout key (partial unique index).
- **Server trusts nothing.** Saves re-evaluate `visibleWhen` (hidden values stripped),
  recompute derived fields (client BMI is ignored), check option membership including the
  cascade parent/child pair, enforce bpPair bounds and `mustBeTrue`, and return 422 with
  per-field errors. Records stamp the exact layout version and use client-generated ids
  (idempotent upsert — the offline-ready seam).
- **One extraction seam.** `/api/extract` = Layer 1 (layout-compiled regex/alias resolver,
  word-to-number 0–500, correction commands `change X to Y` / `clear X`) → routing rule
  (≥1 field & ≥70% consumed → skip LLM, visible in logs and in the `layer` response field) →
  Layer 2 provider (Ollama/OpenAI structured output, confidence clamped ≤0.9) → deterministic
  normalizer (fuzzy option snap, unit coercion lbs→kg/in→cm, ISO date/time).
- **Surface adaptation is CSS + injectable pointer state.** Container queries for width,
  `PointerModeService` (media query default, Maintainer override) for touch variants
  (segmented buttons, steppers, chips, sheet picker). Zero platform conditionals:
  `grep -r "isNative\|isIpad" client/projects/syn-form/src` → only the AudioSource doc comment.
- **populate() merges arrays (union)** so dictating allergies one at a time never loses values;
  confidence < 0.85 renders amber until the user touches the field; cascade-cleared fields go amber.

## Spec deltas (v1.1 — from the adversarial review)

1. `contains` operator added to the closed condition set (multiselect visibility needs it).
2. `mustBeTrue` for checkbox "required means true" (consentVerified).
3. `bpBounds` per-field sys/dia ranges (single min/max can't express them).
4. `<syn-form [layout]>` in-memory input — what makes the Maintainer live preview possible.
5. `form_record.field_values` (not `values` — reserved word in Postgres), plus `context` column.
6. Layout list/create/clone/discard-draft endpoints + UI; records list/get/update/delete.
7. `/extract` takes `layoutVersion` so extraction matches the rendered form version.
8. Deepgram temp-token proxy instead of key-in-browser.
9. Lookups deactivate instead of hard delete (published layouts/records reference them).
10. Optimistic concurrency on draft saves (`expectedUpdatedAt`).

## Not in scope on this machine

- Capacitor/iPad shell (needs Xcode/macOS). The touch rendering is fully testable in-browser
  via the Maintainer "Preview on touch" toggle or device emulation; keyboard avoidance is
  implemented via focus scroll-into-view inside the component.
- Offline sync (deliberately deferred; the seams — data service interface, client ids,
  durable provenance — are in place per the review discussion).
