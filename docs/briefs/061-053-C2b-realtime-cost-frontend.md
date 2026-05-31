# /tdd brief — realtime_cost_frontend (053-C2b; frontend half of the realtime-cost fix)

> **Frontend slice (`web/`).** The frontend half of 053-C2 (realtime cost `n/a`). 053-C2a (backend, `2977f7f`)
> landed the contract: `CompleteTurnRequest` now carries `InputAudioTokens`/`OutputAudioTokens`/`CachedAudioInputTokens`,
> and `EstimateRealtime` prices from those exact DC audio-token counts. **But the realtime frontend path never POSTs
> `/complete`** — it only calls `appendTurnEvents` (`/events`). So the backend's exact-count pricing never runs for a
> realtime turn (the turn ships non-terminal, cost null). This slice extracts `response.done.usage` and POSTs
> `/complete` with the token counts, so realtime cost stops being `n/a`. No safety invariant.

## Feature
Extract the realtime turn's exact audio-token usage from the `response.done` DC frame and POST it to
`/complete`, finalizing the realtime turn on the backend with the exact-count cost estimate. Add the TS
`CompleteTurnRequest` wire mirror.

## The fixture (ground truth — captured live from the user's key)
`docs/runbooks/053c-realtime-dc-capture.md`, event #20 (`response.done`):
```
usage: { total_tokens:139,
         input_tokens:68,  input_token_details:{ text_tokens:37, audio_tokens:31, image_tokens:0,
                                                  cached_tokens:0, cached_tokens_details:{text_tokens:0, audio_tokens:0, image_tokens:0} },
         output_tokens:71, output_token_details:{ text_tokens:17, audio_tokens:54 } }
```
The three fields the contract wants (BE-confirmed + live-confirmed in the fixture):
- `inputAudioTokens`  ← `usage.input_token_details.audio_tokens`   (31)
- `outputAudioTokens` ← `usage.output_token_details.audio_tokens`  (54)
- `cachedAudioInputTokens` ← `usage.input_token_details.cached_tokens` (0)  ← **see Step-2.5 Q5 (precision)**

The source-transcription `…completed` event (#8) carries its OWN `usage` (`total 40, input 31[audio 31], output 9`) —
**out of scope** (transcription cost is a separate disclosed line if ever added; do NOT read or send it here).

## Use case + traceability
- **Task ID:** 053-C2b (realtime cost — frontend). **Architecture:** `ARCH-009`/Appendix A (the `CompleteTurnRequest`
  DTO contract — backend side landed 053-C2a), `ARCH-010 §7` (GA event mapping), `ARCH-014` (realtime cost).
- **Related context:** brief `059-053-C2a` (the backend contract this consumes — read it for the field semantics);
  the runbook fixture above; web lessons **§16** (per-turn event sink), **§17** (the DI'd turn controller),
  **§25** (realtime cost wired at `/complete`, degrade to null never synthetic $0), **§3/§4** (the `http` boundary +
  Vitest fetch-mock mechanics), **§21** (ComparisonSummary reads cost from `GET /session` — that's how the priced
  realtime turn renders after this lands; no store change needed here).

## Pre-orient (confirmed against HEAD `2977f7f` — premises hold)
- `web/src/realtime/realtimeEvents.ts` — `normalizeRealtimeEvent` returns `{ kind: 'responseDone' }` with **NO usage
  payload** today (line ~73). It is the single pure classify point.
- `web/src/realtime/realtimeTurnController.ts` — on `event.kind === 'responseDone'` it calls `reportTurnEvents`
  (→ `appendTurnEvents` → `/events`) and **nothing else** (lines ~150-159). The production singleton wires
  `api.createTurn` + `api.appendTurnEvents` only (lines ~200-209).
- `web/src/realtime/realtimeEventSink.ts` — handles `responseDone` with a **store-only** `completeTurn` (local state);
  it makes NO API call and has NO audio/usage in the store (invariant #3). **Leave the sink unchanged.**
- `web/src/api/sessionsApi.ts` — has `appendTurnEvents` but **NO `completeTurn`** (its own header comment even says
  "/complete … are Phase E" — only `/events` was implemented).
- `web/src/types/domain.ts` — **no `CompleteTurnRequest` TS mirror** (grep-confirmed absent).

## Design (the thin, honest wiring)
1. **Extract usage in the pure normalizer** (not the controller — keep the single classify point, web §15/§16).
   Extend the `responseDone` variant to `{ kind: 'responseDone'; usage: RealtimeUsageTokens | null }` where
   `RealtimeUsageTokens = { inputAudioTokens?: number; outputAudioTokens?: number; cachedAudioInputTokens?: number }`.
   Guard every field independently (each `usage.*` path may be absent/non-number) — malformed/absent `usage` → `usage: null`,
   never throw (the guard-the-body discipline, web §9). **Distinguish `cached=0` (a real value → send 0) from absent
   (→ omit the field)** — the honest-degrade pin (do NOT fabricate a 0).
2. **POST `/complete` from the controller** on `responseDone` (sibling to the existing `reportTurnEvents`). Add a new
   `api.completeTurn(sessionId, turnId, body)` dep; map `event.usage` → the request's token fields (omit any absent
   field, never a synthesized 0); send `status: 'completed'`. A `/complete` failure is **surfaced** (sanitized via the
   `ApiError`/`store.addError` path, mirroring `reportTurnEvents`), never swallowed (ARCH-018).
3. **Add `sessionsApi.completeTurn`** — `POST /api/sessions/{id}/turns/{turnId}/complete`, JSON body = the
   `CompleteTurnRequest` mirror, returns `CompleteTurnResponse` (or the focused shape the impl needs).
4. **Add the TS `CompleteTurnRequest` mirror** to `web/src/types/domain.ts` (full wire shape; the FE only populates the
   token fields + `status`, but type it faithfully).

## Acceptance (RED-first; deterministic — all of this is pure/DI'd, no browser exempt surface)
- [ ] **`normalizeRealtimeEvent` extracts usage from `response.done`** — feed the fixture frame → assert
      `{ kind:'responseDone', usage:{ inputAudioTokens:31, outputAudioTokens:54, cachedAudioInputTokens:0 } }`.
- [ ] **Degrade: `response.done` with absent/malformed `usage`** → `{ kind:'responseDone', usage:null }` (never throws).
- [ ] **Partial usage guarded independently** — e.g. `output_token_details` present, `input_token_details` absent →
      only `outputAudioTokens` set; the others omitted (not 0).
- [ ] **`sessionsApi.completeTurn` POSTs the right URL + body** — `POST …/turns/{turnId}/complete`, JSON body carries
      the token fields; fetch-mock per web §4 (fresh `Response` per call; assert request args).
- [ ] **Controller wires `/complete` on `responseDone`** — DI'd api mock; on a `responseDone` carrying usage, the
      controller calls `completeTurn` with `{ inputAudioTokens, outputAudioTokens, cachedAudioInputTokens, status:'completed' }`.
- [ ] **Controller still calls `appendTurnEvents`** (regression — the existing `/events` report is not broken/replaced).
- [ ] **Honest degrade: `responseDone` with `usage:null`** → the controller still POSTs `/complete` (so the turn
      finalizes terminal) but **without** token fields (backend degrades to its absent-tokens path → disclosed-unavailable
      cost, never a synthetic 0). **Send `cached:0` when real; omit when absent** — pin the distinction.
- [ ] **`/complete` failure is surfaced** — a rejected `completeTurn` → `store.addError` with a sanitized `UiError`
      (`ApiError.uiError` or a fixed fallback code), never swallowed.
- [ ] `/preflight` (web) clean: `format:check && lint && typecheck && test` all green.

## Files expected to touch (`web/`; confirm at Step 1)
**Modified:**
- `web/src/realtime/realtimeEvents.ts` — `responseDone` variant gains `usage`; add the guarded extraction.
- `web/src/realtime/realtimeEvents.test.ts` — usage-extraction + degrade tests.
- `web/src/realtime/realtimeTurnController.ts` — POST `/complete` on `responseDone`; new `api.completeTurn` dep + wire
  it in the production singleton.
- `web/src/realtime/realtimeTurnController.test.ts` — controller wiring + degrade + regression tests.
- `web/src/api/sessionsApi.ts` — add `completeTurn`.
- `web/src/api/sessionsApi.test.ts` — `completeTurn` POST-shape test (if the file exists; else co-locate).
- `web/src/types/domain.ts` — add the `CompleteTurnRequest` TS mirror.

**Leave unchanged:** `web/src/realtime/realtimeEventSink.ts` (store-only completion; invariant #3 stays untouched —
the cost is backend-priced + rendered via `GET /session`, never through the store).

If implementation needs files beyond this list, **flag at Step 2.5** before going GREEN.

## Wiring / entry point (Step 7.5)
`realtimeTurnController.ts` → `startTurn` sets `client.onServerEvent`; on `event.kind === 'responseDone'` the new
`/complete` POST fires. The real path: `RecordingControls` Stop (`currentMode==='realtime'`) → `stopTurn` →
backend `response.create` → DC `response.done` frame → `onServerEvent` → normalize → `completeTurn` POST. Confirm the
**production singleton** at the bottom of the controller wires `api.completeTurn: (s,t,body) => sessionsApi.completeTurn(s,t,body)`
— not just the test DI. (Without that wiring it's tested-but-unreachable.)

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes)
- **New TS wire mirror `CompleteTurnRequest`** → a new **`web/CLAUDE.md` cross-doc row** (mirror-registration; the
  backend `CompleteTurnRequest` + its 053-C2a token fields go in **`ARCHITECTURE.md` Appendix A** — that row is written
  by the orchestrator at round-seal alongside the C2a backend routing, since C2a's doc-routing is not yet landed).
  **No new ARCHITECTURE.md contract** beyond the Appendix-A row C2a already owes.
- The normalizer's `RealtimeUsageTokens` + the `responseDone.usage` field are **frontend-internal** (not a wire mirror) —
  no cross-doc row; just flag the `CompleteTurnRequest` mirror.

> Implementer never edits `web/CLAUDE.md`, `ARCHITECTURE.md`, `MVP_TASKS.md`, or `web/LESSONS.md` — flag at Step 9; orchestrator writes hot.

## Things to flag at Step 2.5
1. **Where to extract usage — pure normalizer vs controller re-parse.** My vote: **the normalizer** (`responseDone`
   gains `usage`) — it's the single pure classify point (web §15/§16); the controller stays thin (reads `event.usage`).
   Confirm vs re-parsing the raw frame in the controller (breaks the classify/orchestrate separation).
2. **`/complete` body scope — tokens + `status:'completed'` only, vs also transcripts / `outputAudioDurationMs`.**
   My vote: **tokens + `status:'completed'` only.** Transcript persistence is a separate concern (out of scope here);
   `outputAudioDurationMs` (the E.2b played-duration approach) is **superseded** by exact-count pricing per brief 059
   — do NOT compute or send it. Confirm.
3. **Honest degrade when `usage` is null — still POST `/complete` (finalize) vs skip it.** My vote: **still POST**
   `/complete` (so the realtime turn finalizes terminal + the backend prices via its absent-tokens path = disclosed-
   unavailable, never a synthetic $0 — web §25). The absent-usage realtime cost honestly degrades to null. Confirm.
4. **Sequencing `/complete` vs the existing `/events` report.** My vote: **keep `reportTurnEvents` unchanged and fire
   `/complete` as an independent sibling** on `responseDone`. The two are independent backend writes (`/events`→`UpdateTurn`,
   `/complete`→idempotent `FinalizeTurn`); order doesn't affect the token-priced cost. Confirm vs await-sequencing them.
5. **⚠️ Cached-token PATH precision — `input_token_details.cached_tokens` (BE-confirmed, the path 053-C2a priced from)
   vs `input_token_details.cached_tokens_details.audio_tokens` (audio-specific, matches the field's `CachedAudioInput`
   semantics).** The fixture has **0 for both**, so it's deterministically indistinguishable at the fixture. My vote:
   **use the BE-confirmed `input_token_details.cached_tokens`** to mirror exactly what the backend contract expects
   (FE/BE must agree on one path or they silently disagree) — and **surface the audio-specific nuance at Step 9** as a
   write-up note (cached TEXT tokens would be mispriced at the audio-cached rate; immaterial while cached=0, but worth a
   line). Confirm — and if Context7 (Realtime API `response.done.usage`) cleanly disambiguates, take the precise path.
6. **Failed-turn `/complete` — in or out of scope?** My vote: **out of scope.** The `error` path (`sink.failTurn`)
   does not reach `responseDone`; finalizing a FAILED realtime turn terminal on the backend is a separate gap (the turn
   never POSTs `/complete` today either). Flag it as a Step-9 carry-forward candidate; do not expand C2b to cover it.

## Dependencies + sequencing
- **Depends on:** 053-C2a (backend, `2977f7f`) — **landed.** The DTO field names (`InputAudioTokens`/`OutputAudioTokens`/
  `CachedAudioInputTokens`) are fixed; this slice mirrors them. Also benefits from 053-C1 (`b5629dc`, first-audio anchor)
  but is independent of it.
- **Blocks:** nothing hard. Once this lands, ComparisonSummary's per-variant cost split (web §21) shows real realtime
  cost from `GET /session`; the G.5 write-up's realtime cost column becomes real.

## Estimated commit count
**1.** The normalizer usage-extraction + `sessionsApi.completeTurn` + controller wiring + the TS mirror are one logical
unit (realtime cost frontend wiring). No safety invariant. The cross-doc TS-mirror row rides the orchestrator's
round-seal (not the impl's slice commit).

## Lessons-logged candidates anticipated
- **Convention candidate** — "realtime cost finalizes at `/complete` (the realtime finalize path) carrying exact DC
  `response.done.usage` audio-token counts; extract usage in the pure normalizer not the controller; degrade to null
  (still finalize) when usage is absent, distinguishing a real cached=0 from absent — never a synthetic $0." (Extends
  web §25 with the frontend extract/POST half.)
- **Architecture-doc note** — ARCH-014/ARCH-010 §7: realtime cost is priced from the DC `response.done.usage` exact
  audio-token counts the frontend forwards at `/complete` (the `output_audio_buffer.started`/`response.done` GA shapes
  are the 053-C confirmed strings).
- **Future TODO candidate** — failed-realtime-turn `/complete` finalize (Step-2.5 Q6); the cached-audio-token path
  precision note (Q5).

## How to invoke
1. Read this brief + `docs/runbooks/053c-realtime-dc-capture.md` (the usage fixture) + brief `059-053-C2a` (the backend
   contract) + ARCH-010 §7 / ARCH-014.
2. Confirm the file:line refs at Step 1.
3. `/tdd realtime_cost_frontend` — RED the normalizer usage-extraction first (against the fixture), then
   `sessionsApi.completeTurn`, then the controller wiring, then the TS mirror.
4. Step 2.5: answer Q1–Q6 (esp. Q5 cached-path precision — optionally confirm via Context7). Step 9: categorized
   summary + the `CompleteTurnRequest` TS-mirror cross-doc flag + the Q5/Q6 follow-ups + ship/no-ship + draft commit message.
