# Local Dev & Test Runbook

> How to start the AI Interpreter Workbench on your machine and exercise it — both the running app (manual smoke) and the automated test suites. For the formal live-validation pass with measured numbers, see `real-key-smoke-runbook.md`; this is the everyday "get it running and poke it" guide.

**Time:** ~5 min to running. **You need:** .NET 8 SDK, Node 22 LTS, a populated repo-root `.env` (provider keys), a Chromium browser, and a working mic for live turns.

---

## 1. Prerequisites

| Need | Version (verified) | Check |
|---|---|---|
| .NET SDK | 8.0.x (pinned by `server/global.json`) | `dotnet --version` |
| Node | 22 LTS | `node --version` |
| Provider keys | OpenAI + Deepgram | see §1.1 |

### 1.1 Keys live in `.env` (server-side only)

The backend reads provider keys from the **repo-root `.env`** (auto-loaded at startup — `DotEnvLoader`, server lesson §33). They are **server-side only** — never in `web/`, never in a response body, never in persisted session JSON (SAFETY invariant #1). If `.env` is missing or a key is blank, that provider's mode degrades to "unavailable" rather than crashing.

Required keys (names only): `OPENAI_API_KEY`, `DEEPGRAM_API_KEY`. The `.env` also carries model/voice/timeout/path settings (`OPENAI_TRANSLATION_MODEL`, `DEEPGRAM_STT_MODEL`, `SESSION_DATA_DIR`, `PRICING_CONFIG_PATH`, etc.) — all have sane inline defaults if absent.

> If you don't have a `.env`, copy the team's or set `OPENAI_API_KEY` / `DEEPGRAM_API_KEY` in your shell before starting the backend.

---

## 2. One-time setup (first clone, or when a manifest changes)

```bash
# Backend deps
cd server && dotnet restore

# Frontend deps
cd ../web && npm install
```

---

## 3. Start the backend (`:5179`)

The backend **must** be on port **5179** — the Vite dev proxy (ARCH-029) forwards `/api` REST + the cascade WebSocket there, so the frontend works zero-config.

```bash
cd server/AiInterpreter.Api
dotnet run --urls http://localhost:5179
```

The repo-root `.env` auto-loads, so plain `dotnet run` works. Ready when you see `Now listening on: http://localhost:5179`.

**Optional dev tweaks** (what the team runs):

```bash
# from server/AiInterpreter.Api, before dotnet run:
set -a && source ../../.env && set +a          # belt-and-suspenders explicit env load
export OPENAI_TRANSLATION_MODEL=gpt-5-nano      # cheap/fast cascade translation model
export Logging__LogLevel__Default=Debug         # verbose logs (provider HTTP calls, etc.)
dotnet run --urls http://localhost:5179
```

> Leave this terminal running. Backend logs (Deepgram WS, OpenAI HTTP, mint calls) stream here — useful when smoke-testing.

---

## 4. Start the frontend (`:5173`)

In a **second terminal**:

```bash
cd web
npm run dev
```

Open **http://localhost:5173/**. Ready when Vite prints `VITE … ready` and the mode/model selectors populate with no error banner.

> **After any backend or Vite restart, hard-refresh the tab (Cmd+Shift+R).** A plain reload can leave the tab on stale Vite HMR ("nothing happens / no console logs"). This is the single most common local gotcha (memory: `vite-restart-hard-refresh`).

---

## 5. Manually test the app (live smoke)

Grant mic permission on first record. Each flow below is a real provider round-trip.

### 5.1 Realtime mode (single live speech-to-speech model)
1. Mode → **Realtime**, Direction **English → Spanish**, Turn control **Manual** (or Auto-VAD).
2. **Start session** → **Start recording** → speak a sentence → **Stop** (Manual) or just pause (Auto-VAD).
3. Expect: the Spanish translation plays back; the Live transcript shows source + target; the Metrics panel shows latency; the Cost panel shows realtime $/min.
4. **Interpreter check:** speak a *question* ("What is your name?"). It must **translate** it ("¿Cómo te llamas?"), not **answer** it.

### 5.2 Cascade mode (STT → Translation → TTS, three observable stages)
1. Toggle to **Cascade** (the session can switch mid-flight).
2. Record a turn the same way.
3. Expect: per-stage latency markers (transcription / translation / audio) in the Metrics panel; cascade $/min in the Cost panel.

### 5.3 Bidirectional + Auto-VAD (hands-free)
1. Enable **Bidirectional** + **Auto-VAD** (either mode).
2. Speak without pressing Stop — it auto-detects end-of-utterance, translates, and re-arms for the next turn. Alternate English/Spanish; the per-turn direction badge flips automatically.

### 5.4 WER evaluation (push-to-talk)
1. **Evaluation · WER** panel → pick a scripted phrase → **Record**.
2. After the **3·2·1 countdown**, read the phrase aloud while it shows "Recording," then **Stop**.
3. Expect a real word-error-rate scored against the phrase. Stay silent → it honestly shows **"no speech detected — n/a"**, never a fabricated 100%.

### 5.5 Comparison + persisted evidence
- The **Comparison** panel shows the apples-to-apples latency/cost/WER once both modes have turns.
- Persisted sessions land as JSON under `data/sessions/` (see §8) — transcripts, per-turn metrics/cost, summary. **No keys, no `ek_` token, no raw audio.**

### 5.6 Soak harness (optional, dev-only)
Open **http://localhost:5173/?soak=1** → pick a mode → **Run soak**. Drives a scripted 5-minute synthetic EN↔ES conversation through the real pipeline (no mic needed — synthetic audio is injected at the capture boundary) → renders a `SoakReport` with the three ARCH-020 stability booleans (no disconnect / no drift / no leak) + latency/cost/WER. Run cascade then realtime, one at a time (the panel resets per run; ~5 min each).

---

## 6. Run the automated tests

### Backend (xUnit) — from `server/`
```bash
dotnet test                                   # full suite
dotnet test --filter FullyQualifiedName~Cost  # one class/area
```

### Frontend (Vitest) — from `web/`
```bash
npm run test                                  # full suite (vitest run)
npm run test -- CostPanel                      # one file by name
```

### Full preflight gates (run before calling a change "done")
```bash
# Backend (from server/)
dotnet format --verify-no-changes && dotnet build && dotnet test

# Frontend (from web/)
npm run format:check && npm run lint && npm run typecheck && npm run test
```

> `format:check` is first and non-optional on the frontend — skipping it lets Prettier drift accumulate (web lesson, session 009).

---

## 7. Stop / restart

```bash
# Stop the backend / frontend: Ctrl+C in their terminals, or by port:
lsof -ti tcp:5179 | xargs kill -9   # backend
lsof -ti tcp:5173 | xargs kill -9   # frontend
```

After restarting **either** server, **hard-refresh the browser tab (Cmd+Shift+R)**. Backend-side code changes (e.g. the realtime interpreter instruction, which is minted server-side) only take effect after a backend restart.

---

## 8. Where things live

| Thing | Path |
|---|---|
| Persisted sessions (JSON) | `data/sessions/session_*.json` (from `SESSION_DATA_DIR`) |
| Backend logs | the `dotnet run` terminal (set `Logging__LogLevel__Default=Debug` for verbose) |
| Pricing config | path in `PRICING_CONFIG_PATH` |
| Evaluation phrases | path in `EVALUATION_PHRASES_PATH` |

---

## 9. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "Nothing happens" / no console logs after a restart | Stale Vite HMR — **hard-refresh (Cmd+Shift+R)**. |
| A mode is greyed out / "unavailable" | That provider's key is missing/blank in `.env`. Set it, restart the backend. |
| Frontend loads but every call errors | Backend not on `:5179` (the proxy target) — confirm `Now listening on: http://localhost:5179`. |
| Backend code change didn't take effect | Restart `:5179` (e.g. the realtime instruction is minted server-side, not hot-reloaded). |
| A turn shows `n/a` for a metric/cost | Often **honest degrade** (unmeasurable / silent turn / failed leg), not a bug — `n/a` is deliberate, never a fabricated number. |
| Port already in use | `lsof -ti tcp:<port> | xargs kill -9` then restart. |
| Realtime question gets *answered* not *translated* | Interpreter-instruction behavior; ensure you're on a backend restarted with the latest mint templates. |
