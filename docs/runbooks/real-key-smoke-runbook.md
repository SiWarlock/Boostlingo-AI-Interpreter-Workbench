# Real-Key Smoke Runbook

> The one live-validation pass. Run the app with **real provider keys**, exercise both modes + both model variants, and capture the measured numbers. This runbook is the input to **G.3** (demo script), the source of the **G.5** `[SMOKE: …]` numbers, and the **final-submission acceptance checklist**. Follow it top to bottom; fill the **§5 Capture Table** as you go.

**Time:** ~25–35 min. **You need:** a Deepgram key, an OpenAI key, a Chromium browser, a working mic.

---

## 1. Prerequisites

### 1.1 Keys + where they go

Two **standard provider keys**, both **server-side only** (SAFETY invariant #1 — they NEVER appear in `web/`, never in a response body, never committed):

| Provider | Used for | Models exercised |
|---|---|---|
| **Deepgram** | Cascade STT | `nova-3` (`multi`) |
| **OpenAI** | Cascade translation + TTS, Realtime | translation `gpt-5-nano` / `gpt-5-mini`; TTS `gpt-4o-mini-tts`; Realtime `gpt-realtime` / `gpt-realtime-mini` |

```bash
# From the repo root — create your local .env (gitignored; NEVER commit it)
cp .env.example .env
```

Then edit `.env` and set **only the two secret values** (everything else has a working default):

```bash
OPENAI_API_KEY=sk-...        # OpenAI standard key — translation + TTS + Realtime mint
DEEPGRAM_API_KEY=...         # Deepgram standard key — Cascade STT
```

The model/config env vars (`OPENAI_REALTIME_MODEL`, `OPENAI_TRANSLATION_MODEL`, `DEEPGRAM_STT_MODEL`, etc., per ARCH-028) already default to the values in the matrix below — you only switch them when §3 T7 asks for the alternate variant. Full list: [`.env.example`](../../.env.example).

> ⚠️ **SAFETY:** the keys live ONLY in `server/`-side env. The browser receives only the short-lived ephemeral Realtime credential (`ek_…`). If you ever see a `sk-`/Deepgram key in the browser devtools, in a `data/sessions/*.json`, or in a UI error — that's a bug; stop + report it. (§6 verifies this.)

### 1.2 Billing note

A real run spends real provider credits — Deepgram STT minutes, OpenAI translation/TTS/Realtime tokens + audio. The full matrix (§3) is a few minutes of audio across both modes + both variants — cents-to-low-dollars, not more. Stop early if a key is rejected (don't loop a failing call).

### 1.3 Versions

- **.NET 8 SDK** (`dotnet --version` → 8.x)
- **Node 22 LTS** (`node --version` → v22.x) + npm

---

## 2. Start sequence

```bash
# Terminal 1 — backend (http://localhost:5179)
# ⚠️ .NET reads PROCESS env vars, not a .env file (no auto-loader yet — tracked as a G fix).
# Source .env into the shell FIRST, then run — else GET /api/config reports configured:false everywhere:
cd server/AiInterpreter.Api && set -a && source ../../.env && set +a && dotnet restore && dotnet run

# Terminal 2 — frontend (http://localhost:5173)
cd web && npm install && npm run dev
```

> **Env-loading note (bash/zsh):** `set -a; source ../../.env; set +a` exports every `.env` var into the process the backend inherits (the `set -a` makes `source`'d assignments exported). A future small backend slice adds `DotNetEnv` so a plain `dotnet run` auto-loads `.env` in Development — until then, the `source` step is required.

**Verify before testing:**

```bash
# Backend health — expect {"status":"ok"}
curl -s http://localhost:5179/api/health

# Config — with BOTH keys set, the provider blocks report configured:true.
# (Presence-only: it reports the boolean + model names, NEVER a key value — invariant #1.)
curl -s http://localhost:5179/api/config
```

- In `GET /api/config`, confirm the OpenAI + Deepgram blocks show `configured: true` (with no keys they'd be `false` and the modes would be disabled in the UI). If one is `false`, the key for that provider isn't loading — recheck `.env` + restart the backend.
- Open **http://localhost:5173**. Both **Realtime** and **Cascade** should be selectable (an unconfigured mode is disabled by design).

> **Secure context / mic:** `localhost` is a secure context, so `getUserMedia` + WebRTC work in dev. (Any non-localhost host would need HTTPS — out of scope for this local smoke.) On first record the browser prompts for mic permission — **Allow**. Use a headset to avoid the TTS output feeding back into the mic.

---

## 3. Test matrix

Run each test; record the numbers in the **§5 Capture Table**. The pass targets are *guidance for the comparison narrative*, not hard gates — capture the real number even if it misses.

> **Phrase bank** (use these so the WER reference matches; from `evaluation-phrases.json` + ARCH-021):
> **EN:** "I need help checking in for my appointment." · "Where is the pharmacy located in this building?" · "Please describe your symptoms to the doctor."
> **ES:** "Necesito ayuda para registrarme para mi cita." · "¿Dónde está la farmacia en este edificio?" · "Por favor describa sus síntomas al doctor."

For every turn: **Start session** (a labelled session, e.g. `smoke-2026-05-31`), pick the mode + direction in Setup, then **Start recording → speak the phrase → Stop**. Watch the live transcript + the metrics/cost panels populate.

### T1 — Cascade, EN→ES
- Setup: mode **Cascade**, direction **EN→ES**, translation model **gpt-5-nano** (default).
- Action: record an EN phrase → Stop.
- Capture: **stt.first_partial / stt.final**, **translation.first_token / final**, **tts.first_audio** (per-stage ms); the **end-to-end** speech-end→first-audio if shown; **estimated cost/min**; confirm **source + target transcripts streamed live** (not one-shot).
- Pass target: end-to-end **< 3s** (< 2s ideal); audible ES playback; per-stage numbers all present.

### T2 — Cascade, ES→EN
- Same as T1 with direction **ES→EN**, an ES phrase. Capture the same per-stage + cost.

### T3 — Realtime, EN→ES
- Setup: mode **Realtime**, direction **EN→ES**, realtime model **gpt-realtime** (default).
- Action: record an EN phrase → Stop (manual turn).
- Capture: **realtime connect ms** (first turn only), **speech-end → first-audio** ms, source/target transcripts (or the "source unavailable" note if input-transcription is off), **estimated cost/min** (note: realtime prices **input** audio; output is disclosed-unavailable in the cost assumptions — that's expected).
- Pass target: speech-end→first-audio **< 1.5s**; audible ES playback.

### T4 — Realtime, ES→EN
- Same as T3 with direction **ES→EN**, an ES phrase.

### T5 — Mode toggle mid-session (Flow G)
- In an active session, **switch Cascade ↔ Realtime** via the mode toggle, then do one turn in the new mode.
- Capture: no **double-mic** (only one mic active after the switch), no orphaned audio, the new mode records cleanly. PASS = clean switch + a working turn after it.

### T6 — WER evaluation
- Open the **Evaluation panel** → pick a scripted phrase (e.g. `en_001`) → **Record & evaluate** → read the reference aloud.
- Capture: the **WER %** + S/I/D/N; confirm the **"WER is STT-only" explanation** renders; confirm the score **persists** (it'll show in the session JSON + the comparison's WER summary).
- Repeat for **1 ES phrase** (e.g. `es_001`). PASS = a score returns + persists.

### T7 — Both model variants (the comparison's point)
Re-run a short turn under each alternate variant so the comparison has both:
- **Realtime mini:** set realtime model to **gpt-realtime-mini** (Setup selector, or `OPENAI_REALTIME_MODEL=gpt-realtime-mini` + restart) → 1 turn → capture cost/min + latency.
- **Translation mini:** set translation model to **gpt-5-mini** (Setup selector, or `OPENAI_TRANSLATION_MODEL=gpt-5-mini` + restart) → 1 Cascade turn → capture cost/min + latency.
- PASS = a cost/min number for each of the **4 variants** (realtime gpt-realtime / -mini; cascade nano / -mini).

---

## 4. 5-minute stability run (ARCH-020)

Do ~5 minutes of continuous back-and-forth (alternate modes + directions, a dozen+ turns). Watch for the **three** failure signs:

1. **No disconnect** — the WebRTC/WS connection stays up the whole time (a realtime disconnect should surface a sanitized "switch to Cascade" advice, not a silent hang).
2. **No audio drift / overlap** — TTS playback stays aligned with turns; no overlapping or runaway audio; a single reused `AudioContext`.
3. **No memory leak** — open the browser task manager / devtools memory; the tab's memory shouldn't climb unbounded across the run (stop tracks + dispose the pc on End).

Record PASS/FAIL + any observation per check in the §5 table. (If a leak/drift shows, the backend impl is on standby to fix.)

---

## 5. Capture table — fill this in

> Each row maps a measured number to its **G.5 `[SMOKE: …]`** placeholder + the acceptance check. Source every number from the in-app panels / `data/sessions/*.json` — never estimate by hand.

### Latency (avg ms)
| Metric | Realtime | Cascade | → G.5 |
|---|---|---|---|
| Connect (realtime, first turn) | ____ | n/a | §3.1 |
| Speech-end → first audio | ____ | ____ / n/a | §3.1 |
| STT first-partial / final | n/a | ____ / ____ | §3.1 |
| Translation first-token / final | ____ / n/a | ____ / ____ | §3.1 |
| TTS first-audio | ____ | ____ | §3.1 |

### Cost (est. $/min) — by mode AND variant
| Mode | Variant | $/min | → G.5 §3.2 |
|---|---|---|---|
| Realtime | gpt-realtime | ____ | ▢ |
| Realtime | gpt-realtime-mini | ____ | ▢ |
| Cascade | gpt-5-nano | ____ | ▢ |
| Cascade | gpt-5-mini | ____ | ▢ |

### Quality + counts
| | Realtime | Cascade | → G.5 §3.3 |
|---|---|---|---|
| WER (avg, n) | ____ | ____ | ▢ |
| Error count | ____ | ____ | ▢ |
| Turn count (interpretation only — eval turns excluded, F.4) | ____ | ____ | ▢ |

### Acceptance checklist (final-submission gate)
- ▢ Both modes produce **audible interpreted output** EN↔ES.
- ▢ Cascade streams **per-stage** latency (stt/translation/tts) + live transcripts.
- ▢ Realtime speech-end→first-audio captured; cost shows (input-priced, output disclosed-unavailable).
- ▢ **Both model variants** per mode have a cost/min number (4 total).
- ▢ Mode toggle (Flow G) is clean — **no double-mic**.
- ▢ WER returns + **persists** for ≥1 EN + ≥1 ES phrase.
- ▢ Comparison panel shows by-mode + by-variant + WER + **exact turn counts** (F.4).
- ▢ 5-min stability: no disconnect / no drift / no leak.
- ▢ **§6 safety verify passes.**

---

## 6. Safety verify (do this after at least one full session)

The persisted session JSON must contain **no secret + no raw audio** (the sentinel invariant, now observable on real data):

```bash
# Find the latest session file
ls -t data/sessions/*.json | head -1

# It MUST contain transcripts/metrics/errors — and NONE of these. Expect ZERO matches:
grep -iE 'sk-|"apikey"|"clientsecret"|ek_|"audio"\s*:\s*"[A-Za-z0-9+/]{40,}|"bytes"' "$(ls -t data/sessions/*.json | head -1)"
```

- **Zero matches** = PASS (no standard key, no `ek_` ephemeral secret, no base64 audio blob, no raw bytes).
- The file SHOULD have `transcripts`, `latencyEvents`, `costEstimate`, `werResult`, `errors`, `summary` — confirm the legitimate content is there.
- Also spot-check the **browser devtools → Network**: the `ek_…` from `POST /api/realtime/client-secret` is fine in the browser (it's meant to be there); a `sk-`/Deepgram key anywhere browser-side is a **FAIL** — report it.

---

## 7. Troubleshooting

| Symptom | Likely cause | What to do |
|---|---|---|
| A mode is **disabled** in the UI | that provider's key isn't loaded (`/api/config` → `configured:false`) | check the env var name in `.env`, restart the backend |
| **Mic permission denied** | browser blocked `getUserMedia` | the UI shows a recovery hint + Start is disabled; re-allow mic in the browser site settings, reload |
| A turn errors with a **sanitized message** (e.g. `*.rate_limited`, `*.timeout`, `realtime.auth`) | invalid/missing/throttled key, or a provider hiccup | the error is sanitized by design (no stack/secret) — fix the key / wait out the rate limit; a missing-key path returns a clean error, not a crash |
| **Realtime won't connect** (WebRTC) | network/firewall blocks WebRTC, or the `ek_` mint failed | check `POST /api/realtime/client-secret` succeeded (Network tab); the documented fallback is to **use Cascade** for the demo (the UI advises this on a disconnect) |
| **Cascade WS won't connect / no transcripts** | WS blocked, or STT key issue | check the `/api/cascade/stream` WS in the Network tab; the documented fallback is the **blob path** (`POST /api/cascade/turn`, MSE/Chromium) |
| **TTS output feeds back into the mic** | speakers + open mic | use a headset |
| `gpt-5-mini` translation cost looks wrong | `pricing.json` version/rates out of sync | confirm `config/pricing.json` shows version `2026-05-31-payg-estimates`, `gpt-5-mini` 0.25/2.00 |

---

> When the matrix + the §5 table are filled and §6 passes: hand the captured numbers back to the orchestrator — they replace the `[SMOKE: …]` placeholders in `docs/COMPARISON_WRITEUP.md` (G.5) and seed `docs/DEMO_SCRIPT.md` (G.3).
