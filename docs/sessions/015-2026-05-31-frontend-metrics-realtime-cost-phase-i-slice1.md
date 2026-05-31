# Session 015 — Frontend: metrics-correctness, realtime cost, Phase-I realtime slice 1

- **Date:** 2026-05-31
- **Phase:** G.4 (real-key-smoke bug-fix arc) + 053-C (realtime metrics/cost) + Phase I (auto-VAD, realtime half)
- **Area:** frontend (`web/`)
- **Predecessor:** [014 — H.1 UI baseline + G.4 frontend bug-fixes](014-2026-05-31-h1-ui-baseline-and-g4-frontend-bugfixes.md)
- **Successor:** _(next FE session — picks up 063 slice 2; TBD)_

> Implementer session doc (technical close-out only). `MVP_TASKS.md` / `web/LESSONS.md` / `web/CLAUDE.md` / `ARCHITECTURE.md` are orchestrator-owned and untouched here. Cross-doc + lesson items are **surfaced** below for `/orchestrate-end` (the orchestrator is hot-routing them — `ARCHITECTURE.md`/`web/CLAUDE.md`/`web/LESSONS.md` show as modified in the working tree).

## Why this session existed

Continuation of the real-key-smoke bug-fix arc + the realtime-completion work (wholesale cycle from handoff 004). The live smoke surfaced integration bugs invisible to fake tests (wrong per-turn metrics, realtime cost `n/a`, a mode-toggle UX break); the realtime metrics/cost gaps (053-C) needed the live DC capture to pin GA shapes; and Phase I (auto-VAD) was approved to begin. Seven frontend slices landed across these threads.

## What was built (per slice — all `/tdd`, RED-first)

**054 — self-recovering mode toggle** (`92f533a`)
- `state/sessionActions.ts` `switchMode`: a **not-live denylist gate** (`sessionId null || sessionStatus ended/ending` → store-only write, no POST) so a post-end toggle no longer 404s a dead `/mode` endpoint + strands the UI; self-clears prior errors at the start of a real switch (clear-before-retry, §7); normalizes any failure to one sanitized `session.mode_switch_failed`. `components/ModeToggle.tsx` reverted to a thin dispatch. `components/errorCopy.ts` mapped the new code. (Key call: the gate is a *denylist* not `=== active`, so a future live `readyForTurn` status still POSTs — pinned by a regression-guard test.)

**056-c1 — cascade per-turn metrics correctness** (`7e5440b`)
- `state/selectors.ts` `deriveTurnMetrics`: derives per-stage **durations** (STT/Translation/TTS) via absolute-timestamp `between()` over the ARCH-013 stage markers (`deriveStageDurations`), replacing the broken raw-`relativeMs` passthrough (keys never matched the panel); omits absent/negative (same-clock mis-stamp) durations. Re-anchors cascade `speech_end_to_first_audio_ms` to **`stt.final`** (Deepgram endpointing ≈ true speech-end) instead of the manual `recording.stopped` (held after speech → negative); scoped to first-audio ONLY (`speech_end_to_playback` keeps the literal anchor, backend-consistent). `components/latencyTarget.ts` `latencyTier`: negative → `na` (disclosed value, muted badge). `components/MetricsPanel.tsx`: headline = responsiveness for both modes + the `<target` badge (total-turn secondary, no badge); consolidated the two per-stage sections into one labeled "Cascade stages".

**056-c2 — comparison model-variant attribution** (`b73db9a`)
- `state/comparisonActions.ts` `ComparisonData` gains `models: {cascade,realtime}|null` from the session `providerProfile` (cost-independent — works despite the bug-5 cost gap); `components/ComparisonSummary.tsx` `ModeColumn` renders a "Model" line.

**058 — realtime first-audio anchor (053-C1)** (`b5629dc`)
- `realtime/realtimeEvents.ts`: normalize `output_audio_buffer.started` → a payload-less `outputAudioStarted` (the DC event that fires under WebRTC; `response.output_audio.delta` never arrives — audio rides the media track). `realtime/realtimeEventSink.ts`: stamp the existing `realtime.first_audio_delta` + `playback.started` markers on it via a shared `stampFirstAudioOnce()` per-turn latch (audioDelta kept as the fallback). Selector unchanged → the realtime headline populates.

**060 — eval empty-blob guard** (`aebca86`)
- `state/evaluationActions.ts` `runEvaluation`: reject a zero-byte `recordBlob` (`capture.blob.size === 0`) before the paid `/transcribe` POST → a distinct actionable `capture.empty` error. `components/errorCopy.ts`: mapped BOTH `capture.empty` AND the pre-existing `capture.failed` (the §20 null path was silently hitting the generic fallback). Defensive hardening — **NOT** the WER-fabricated-100% fix (an empty blob yields WER ≈ 1.0, the opposite symptom).

**053-C2b — realtime cost frontend** (`8acaf58`)
- `realtime/realtimeEvents.ts`: `responseDone` variant gains `usage: RealtimeUsageTokens | null`; `extractRealtimeUsage` reads the exact audio-token counts from `response.done.usage` (dual-read `response.usage ?? top-level usage`; each field number-guarded; real cached=0 kept vs absent omitted; honest degrade to null). `realtime/realtimeTurnController.ts`: a `finalizeTurn` sibling POSTs `/complete` on responseDone with the token counts + `status:'completed'` (sanitized `realtime.complete_failed` on failure) + the production-singleton wiring. `api/sessionsApi.ts`: `completeTurn`. `types/domain.ts`: `CompleteTurnRequest` + `CompleteTurnResponse` mirrors. The sink stays store-only (invariant #3); cost reads back via `GET /session` (§21). Closes 053-C2 e2e (with the backend C2a `2977f7f`).

**063 slice 1 — Phase-I realtime server-VAD (toggle + config)** (`3ff6bed`)
- `types/domain.ts`: `TurnControlMode = 'manual'|'auto'` union + `UiSessionState.turnControlMode`. `state/sessionStore.ts`: initial `'manual'` + `setTurnControlMode`. `realtime/realtimeTurnController.ts`: `SERVER_VAD_*` constants + the `session.update` `turn_detection` branch (auto → `server_vad`, manual → null; 053-B transcription re-assert preserved) + a per-turn `settled` latch (single-utterance bound — idle after the first auto `response.done`; DEV `console.warn` at the boundary) + auto `stopTurn` no-op (server owns segmentation; a commit would race the auto-commit). `components/SessionSetup.tsx`: a Manual|Auto-VAD toggle (gated mid-turn via `canToggleMode`). Revises ARCH-003 (auto-VAD additive; manual persists).

## Decisions made
- **054 gate = not-live denylist, NOT `=== active`** — a future live `readyForTurn` status must still POST (else silent 2c divergence); with the recovery fixes a hypothetical non-live-status POST self-recovers, whereas an allowlist's failure mode is silent data corruption (worse).
- **056 speech-end re-anchor scoped to first-audio only** — `speech_end_to_playback` keeps the literal `recording.stopped` anchor for backend-`avgSpeechEndToPlaybackMs` consistency (code-quality review caught the over-scope).
- **056/060 model + capture attribution sourced cost-INDEPENDENTLY** — works despite the backend cost gap (bug 5 / C.2 usage-nesting).
- **053-C2b dual-read `response.usage ?? usage`** — the runbook trims the `response:` wrapper; GA nests usage under `response.usage` — the dual-read is robust either way (verify-the-live-GA-shape, §15/§26), pinned by both-shape tests.
- **063 SPLIT** — slice 1 (toggle + config + skip-commit) is event-sequence-INDEPENDENT (reuses the existing single-turn `response.done` finalize) → ships without a live server-VAD capture; the multi-segment lifecycle (slice 2) is quarantined with the live-sequence dependency + the sink-change risk. Auto Stop = no-op (early-end races the server auto-commit). Single-utterance bound via a per-turn `settled` latch + a DEV warn.

## Decisions explicitly NOT made (deferred)
- **⭐ 063 slice 2 — the multi-segment auto-turn lifecycle (the main FE follow-up).** GATED on a **live server-VAD capture** to pin the buffer-event sequence (`input_audio_buffer.speech_started`/`speech_stopped`/`committed` → auto `response.created`/`response.done`) — the captured fixture (`docs/runbooks/053c-realtime-dc-capture.md`) is MANUAL mode and does NOT show it. Scope: normalize the buffer events (guarded), `beginTurn` per server-detected segment (multiple turns per recording session), the per-auto-turn `/complete`+`/events`, the auto-mode realtime metrics speech-end anchor, + the guarded early-end override. The DEV warn (`[realtime auto-VAD] …`) fires at the slice-1 single-utterance boundary — capture the live `oai-events` log on the next realtime auto-VAD smoke.
- **053-C2 backend follow-ups (057/059, BE-led):** cascade cost usage-nesting; the coincident `tts.started==tts.first_audio` stamp (bug 2); the session-avg `avgSpeechEndToFirstAudioMs` divergence (the per-turn re-anchored to stt.final but the backend aggregate still uses recording.stopped — surfaced 056-c1).
- **The WER-fabricated-100% fix** — diagnosis-gated on a live repro fixture (the lead is capturing it); 060 is separate hardening, NOT this.
- **063 slice-1 edges (→ slice 2 working set):** the mode-flip-after-disconnect race (a flip to manual mid-auto-turn after a disconnect could send a stray commit — §18 reconnect territory); the silent 2nd-utterance drop (now DEV-warn-observable; slice 2 resolves by handling multi-segment).

## TDD compliance
**Clean.** Every slice was RED-first via `/tdd` (the per-stage derivation, the re-anchor, the normalizer extractions, the controller wiring, the store/toggle, the guards). Manual-smoke-exempt (per posture): only the 063 DEV `console.warn` (a 053-B-style dev logger — no test). Review-surfaced findings folded in-slice with their own RED-first tests where they changed behavior (054 self-clear, 056 omit-negative, 063 latch-reset). No violations.

## Reachability
All features reachable from production entry points (Step 7.5 each): 054 ModeToggle→switchMode→ErrorBanner; 056-c1 App→MetricsPanel→deriveTurnMetrics/latencyTier; 056-c2 App→ComparisonSummary→loadComparison→ModeColumn; 058 DC frame→normalize→sink→deriveTurnMetrics→MetricsPanel; 060 EvaluationPanel→runEvaluation→recordBlob→guard; 053-C2b RecordingControls Stop→controller→completeTurn (**production singleton wired**, not just test DI); 063 SessionSetup toggle→setTurnControlMode→controller branch (production singleton). **No tested-but-unwired gaps.**

## Open follow-ups (Step-9 categorized — orchestrator routes; surfaced for `/orchestrate-end` verification)
- **Cross-doc (orch writes; in progress per the working tree):** `CompleteTurnRequest`/`CompleteTurnResponse` mirrors (053-C2b) → `web/CLAUDE.md` rows + ARCH-009 Appendix A (rides C2a routing); `TurnControlMode` union + `UiSessionState.turnControlMode` (063) → `web/CLAUDE.md` row + ARCH-007 §4 + ARCH-005 union + **the ARCH-003 revision note**. 054/056/058/060 added only frontend-local codes/types (no wire-model change).
- **Lessons (orch writes):** the not-live-denylist+self-clear gate (054); the per-stage-derivation + stt.final re-anchor (056); the buffer-started first-audio anchor refining §23 (058); the empty-blob guard refining §20 (060); the realtime `/complete` cost extract/POST extending §25 (053-C2b); the auto-VAD pattern extending §17 (063, §27).
- **Future TODO — phase (orch → Carry-forward):** 063 slice 2 (multi-segment lifecycle, gated on a live capture); the 053-C2 backend follow-ups (057/059); the slice-1 edges (disconnect-race, auto metrics anchor); the WER fix (live-repro-gated).
- **Wiring:** none outstanding (all reachable).

## How to use what was built
- Run `npm run dev` (web) + the backend with `.env` sourced. Auto-VAD: in SessionSetup pick **Auto-VAD**, Start, and talk — the server detects speech-end + responds (no manual Stop needed) for a single utterance; the browser console shows `[realtime auto-VAD] …` if a 2nd utterance is spoken (the slice-1 boundary). For the slice-2 capture, grab the `[realtime oai-events]` DC log during that auto-VAD turn.
