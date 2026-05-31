# AI Interpreter Workbench

> **One UI, two mode-specific transports, one normalized session + metrics model, one persisted evidence trail — an instrumented comparison workbench, not a production interpreter.**

A browser workbench that builds and instruments **OpenAI Realtime** vs a **streaming STT→Translation→TTS cascade** to compare live-interpretation **latency, cost, and quality** under identical conditions. The product is *measured evidence* (latency events, estimated cost/min, WER) — used to make an architecture recommendation, not to ship a production interpreter.

For the full design contract see **[`ARCHITECTURE.md`](ARCHITECTURE.md)**; the phase plan + build state live in **[`MVP_TASKS.md`](MVP_TASKS.md)**.

---

## What it does

Two interpretation modes behind one mode-agnostic UI:

| | **Realtime** | **Cascade** |
|---|---|---|
| Transport | Browser **WebRTC** to OpenAI, authorized by a backend-minted ephemeral credential (`ek_…`) | A fully **streaming** pipeline: live mic → STT → translation → TTS |
| Models | `gpt-realtime` / `gpt-realtime-mini` | Deepgram `nova-3` (`multi`) STT → OpenAI `gpt-5.4-nano`/`-mini` translation → OpenAI `gpt-4o-mini-tts` |
| Latency profile | One end-to-end model hop | Per-stage (STT partial → translation token → TTS first-audio), each measured |

The shared evidence layer: a normalized `LatencyEvent` schema, a config-driven cost estimator, a scripted **WER** utility, and local JSON session files (**no raw audio, no secrets**). A standalone **Evaluation panel** scores STT against scripted phrases; a **Comparison summary** aggregates latency/cost/WER **by mode and by model variant**.

---

## Prerequisites

- **.NET 8 SDK** (C# 12) — the backend
- **Node 22 LTS** + npm — the frontend (React 19 + Vite + TypeScript)
- An **OpenAI API key** (Realtime + translation + TTS) and a **Deepgram API key** (STT)
- A modern Chromium browser (the demo path uses MSE + WebRTC)

---

## Setup (clean clone → run)

```bash
# 1. Configure secrets (NEVER commit a real key)
cp .env.example .env
#    then edit .env and fill in OPENAI_API_KEY + DEEPGRAM_API_KEY

# 2. Backend  (http://localhost:5179)
cd server/AiInterpreter.Api && dotnet restore && dotnet run

# 3. Frontend (http://localhost:5173)  — in a second terminal
cd web && npm install && npm run dev

# 4. Open http://localhost:5173
```

The SPA calls `GET /api/config` on load and **disables any mode whose provider key is missing** — so a partial `.env` (e.g. only OpenAI) still runs, with Cascade STT degraded. Standard provider keys stay **backend-only**; the browser only ever receives the short-lived ephemeral Realtime credential (`ek_…`).

> **Secure context:** `localhost` is a secure context, so mic capture + WebRTC work out of the box in development. **Any non-localhost host needs HTTPS** for `getUserMedia`/WebRTC (browsers block mic access on insecure origins).

---

## Configuration (`.env`, ARCH-028)

| Variable | Purpose |
|---|---|
| `OPENAI_API_KEY` | OpenAI standard key — backend only (Realtime mint, translation, TTS). Never sent to the browser. |
| `DEEPGRAM_API_KEY` | Deepgram standard key — backend only (Cascade STT). |
| `OPENAI_REALTIME_MODEL` | `gpt-realtime` or `gpt-realtime-mini` |
| `OPENAI_TRANSLATION_MODEL` | `gpt-5.4-nano` or `gpt-5.4-mini` |
| `OPENAI_TTS_MODEL` / `OPENAI_TTS_VOICE` / `OPENAI_TTS_FORMAT` | Cascade TTS config |
| `DEEPGRAM_STT_MODEL` / `DEEPGRAM_STT_LANGUAGE` | Cascade STT config (`nova-3` / `multi`) |
| `*_TIMEOUT_SECONDS` | Per-stage timeouts (STT / translation / TTS / realtime-token) |
| `CASCADE_MAX_UPLOAD_BYTES` / `EVAL_MAX_UPLOAD_BYTES` | Audio-upload caps (~10MB; eval falls back to the cascade cap) |
| `SESSION_DATA_DIR` | Where session JSON is written (default `data/sessions/`) |
| `PRICING_CONFIG_PATH` | The cost-estimate pricing table (`config/pricing.json`) |
| `EVALUATION_PHRASES_PATH` | Override for the scripted WER phrases (defaults to the shipped file) |

The full list with defaults is in [`.env.example`](.env.example).

---

## Using the workbench

1. **Setup** — pick a mode (Realtime/Cascade), a language direction, and the model variants (unconfigured modes are disabled). Start the session.
2. **Record a turn** — Start/Stop delimits a turn; audio streams within it. Source + target transcripts render live; per-stage latency + estimated cost/min populate.
3. **Switch modes / models** — compare the same phrases across Realtime vs Cascade and across model variants.
4. **Evaluate (WER)** — in the Evaluation panel, pick a scripted phrase, read it aloud, and get a Word Error Rate score for the STT transcript.
5. **Compare** — the Comparison panel aggregates avg latency, estimated cost/min, errors, WER, and turn counts **by mode and by model variant**.
6. **Inspect** — each session is written to `data/sessions/<id>.json` (transcripts, latency events, cost estimates, WER, errors — never raw audio or keys).

A full walk-through is in **`docs/DEMO_SCRIPT.md`** _(Phase G.3 — pending)_.

### What the metrics mean (ARCH-013)

- **Latency events** are stamped on the *real* first arrival of each provider event (`stt.first_partial`, `translation.first_token`, `tts.first_audio`, realtime `first_audio`) — never synthesized. Aggregates are computed from absolute timestamps (cross-clock-safe); a missing endpoint renders **`n/a`**, never `0`.
- **Estimated cost/min** is a **config-driven estimate from `config/pricing.json`, NOT a bill.** It reflects the model used and the measured token/audio usage. Treat the numbers as relative-comparison signals, not invoices.
- **WER (Word Error Rate)** is an objective **STT-transcript** quality signal against a known scripted phrase. It is **not** a measure of translation quality — a low WER means the recognizer heard the words; it says nothing about whether the translation was good.

---

## Known limitations

- **Cascade end-to-end latency (speechEnd→playback) is shown as `n/a`** — the cascade transport has no client→server latency-report channel (the comparison still shows the cascade per-stage backend latencies + the realtime end-to-end timing).
- **Cost figures are estimates** (config-driven), not provider billing.
- **WER is STT-only**, by design.
- **Single trusted user** — no auth/accounts; the app is a local evaluation tool, not a multi-tenant service (ARCH-002/019). Don't expose it publicly without adding a gate.
- The Spanish TTS voice is an English-leaning OpenAI voice; **MSE/Chromium is the demo playback path** (the non-MSE blob fallback is best-effort).
- Real-provider behavior (live latency/cost numbers, the exact GA event shapes) is confirmed via a manual **real-key smoke**, not unit tests — see the demo checklist.

---

## Data + security notes

- **Session JSON is sensitive — do not commit it.** It holds transcripts and metrics from real runs (`data/sessions/` is gitignored). Treat it as you would any captured-conversation log.
- **Raw audio is never persisted** — session files hold transcripts/metrics/errors only.
- **Secrets never cross the boundary** — standard provider keys are backend-only; the ephemeral Realtime `ek_…` lives only in the browser's live WebRTC session and is never persisted. Provider errors are sanitized (no stack traces, no secrets, no raw payloads) before reaching the UI.

---

## Development

```bash
# Backend (cwd: server/)
dotnet build            # nullable + analyzers as errors
dotnet test             # xUnit
dotnet format --verify-no-changes

# Frontend (cwd: web/)
npm run format:check && npm run lint && npm run typecheck && npm run test
```

Repository layout, the domain model, provider abstractions, and the full API contract are in **[`ARCHITECTURE.md`](ARCHITECTURE.md)** (see the anchor index at the top). Backend + frontend conventions live in `server/CLAUDE.md` and `web/CLAUDE.md`.

---

## Tech stack

| Layer | Backend (`server/`) | Frontend (`web/`) |
|---|---|---|
| Runtime | .NET 8 / C# 12 | Node 22 / TypeScript 5 |
| Framework | ASP.NET Core Web API | React 19 + Vite |
| Tests | xUnit | Vitest |
| Lint / types | `dotnet format` + analyzers / `dotnet build` (nullable) | ESLint / `tsc --noEmit` |
