# Session 022 — Frontend: Phase J bidirectional + cascade continuous-listening + smoke-cluster fixes

**Date:** 2026-06-01
**Phase:** Phase J (Bidirectional) — FE half + the Phase-J live-smoke fix cluster (Finding 1 + Finding 2 FE halves).
**Role:** frontend implementer (`web/`).
**Predecessor:** [018-2026-05-31-frontend-realtime-revival-h3-drillin-cost-polish.md](018-2026-05-31-frontend-realtime-revival-h3-drillin-cost-polish.md)
**Successor:** _(fresh FE — to be respawned by the lead for the G.4 soak-harness build; link on spawn)_

---

## Why this session existed

Phase J makes the workbench a first-class **bidirectional, hands-free** interpreter: within one live session, per-utterance auto-detect the spoken language → translate to the OTHER language → VAD speaker-stop, in BOTH modes. The FE half wired the enable-flag + per-turn direction (080), rendered both-directions transcripts (081), and completed cascade auto-VAD to a continuous loop (082). The user's Phase-J live smoke then surfaced two findings, whose FE halves were fixed here: a phantom/orphan continuous-loop turn (084) and history-drill-in display gaps (085).

The backend half landed in parallel (J.1 cascade direction-flip `33726ec`, J.2 realtime bidirectional instruction `574ad19`, BE-083 empty-turn → Completed-silence) — this session built strictly against the finalized wire contract in `docs/bidirectional-phase-design.md`, in parallel, never blocked on the BE.

## What was built

### Files created
- `web/src/util/detectLanguage.ts` (+ `.test.ts`) — deterministic best-effort EN/ES heuristic (`'en' | 'es' | null`); ¿/¡ + diacritics (strong) → es, else ES/EN function-word density, tie/none → null. Used ONLY for the realtime direction *display* badge (no provider language tag). [080]
- `web/src/components/DirectionBadge.tsx` (+ `.test.tsx`) — pure per-turn direction badge ("EN → ES" / "ES → EN") from `turn.direction`. [081]
- `web/src/components/TurnCard.tsx` (+ `.test.tsx`) — one turn's card: a `DirectionBadge` above the side-by-side source/target columns (the moved-verbatim private `TranscriptColumn` + the realtime source-unavailable note, PRD must-have 6). [081]

### Files modified
- `web/src/types/domain.ts` — `UiSessionState.bidirectional: boolean` + `RealtimeTokenRequest.bidirectional?` (TS wire-mirrors). [080]
- `web/src/state/sessionStore.ts` — `bidirectional` state/patch + `setTurnDirection` action (updates the live turn's direction). [080]
- `web/src/cascade/cascadeStreamClient.ts` — `CascadeStartParams.bidirectional` (omit-when-false) + the new `{type:"direction"}` dispatch case → `setTurnDirection`. [080]
- `web/src/state/recordingActions.ts` — passes `bidirectional` in the cascade start frame [080]; the **cascade continuous-listening re-arm loop** (`rearmCascadeTurn`, the `onCascadeTerminal` re-arm-vs-end branch via `store.turnStatus`, the shared `startTurnFrame`, the auto-mode `stopRecording` end-control, the `userEnded` closure flag) [082].
- `web/src/realtime/realtimeTurnController.ts` — `maybeResolveRealtimeDirection` (heuristic on the authoritative `sourceTranscriptCompleted`) wired into the manual + auto event routes. [080]
- `web/src/api/realtimeApi.ts` — mint body includes `bidirectional` when set. [080]
- `web/src/realtime/realtimeWebRtcClient.ts` — singleton `mint` callback forwards `state.bidirectional`. [080]
- `web/src/components/SessionSetup.tsx` — the "Bidirectional / auto-detect" toggle (enabling it defaults turn-control to Auto-VAD, gated on `canToggleMode` so it can't flip mid-turn). [080]
- `web/src/components/TranscriptPanel.tsx` — restructured single-turn grid → a chronological `TurnCard` stream over `[...turns, currentTurn]`. [081]
- `web/src/styles/workbench.css` — `.tx-stream`/`.tx-card`/`.tx-card-hd`/`.dir-badge` (reusing existing tokens; the stream scroll-cap). [081]
- `web/src/components/SessionDetail.tsx` — per-turn **Model from the authoritative session config per mode** (realtime → `providerProfile.realtimeModel`; cascade → `translationModelUsed ?? providerProfile.translationModel`). [085]
- `web/src/components/ComparisonSummary.tsx` — realtime STT/Translation/TTS-final rows → a single "Single model — no discrete stages" note (mirrors 074's MetricsPanel). [085]
- `web/src/state/recordingActions.test.ts` — +4 J.6 characterization guards for the continuous-loop end-path. [084]

## Decisions made
- **`detectLanguage → 'en' | 'es' | null`** (not the brief's two-value return) — null = ambiguous, so the "fall back to the configured source" policy lives at the *call site*, keeping the pure fn honest. Excluded `'no'`/`'si'` from the Spanish set (common English → false-positive `'es'`; caught by a code-quality HIGH).
- **Direction-attribution asymmetry:** cascade rides the **measured** backend `{type:"direction"}` signal; realtime uses a **best-effort** client heuristic on the completed source transcript (no provider language tag) — documented as a known limitation.
- **Cascade continuous-listening is FE-only, one-file:** `store.turnStatus` (set by `completeTurn`/`failTurn` *before* the terminal hook) distinguishes a re-armable `completed` from a `failed` terminal → no `cascadeStreamClient`/wire change. The mic stays alive across turns (re-open only the WS); `userEnded` is a controller closure (no store bit). Stop button = the auto-mode "end conversation" control.
- **084 + the 085 cost-half are characterization slices** (no production change): 082 was already correct for the continuous end-path race (the orphan is unpreventable on the FE — the `createTurn` POST is in-flight — and left cleanly, never a no-op `client.stop`); and the lead's real session JSON proved completed cascade turns carry a per-turn `estimatedUsdPerMinute` that `formatCostPerMinute(v.cost)` already displays (the Cost=n/a was the FAILED turn = honest n/a). Both dropped speculative code (the defense pre-check; the session-summary fallback) — no code without a RED.

## Decisions explicitly NOT made (deferred)
- **The race-orphan proper finalize** (a created-but-never-WS-started continuous turn) — left to a **BE "finalize pending turns on session `/end`"** follow-up (orch-routed to the deferred-hardening backlog). The FE can't unsend the in-flight POST; the orphan is benign (BE-083 0-transcript exclusion).
- **Mid-session bidirectional propagation** — pre-session-consumed for this round (value read at session/turn start, like the Direction selector).
- **`DirectionBadge` in `SessionDetail`** (history drill-in) — a consistency follow-up (opportunistic G-polish), out of 081's scope.
- **The inter-turn continuous-loop gap** — ships FE-only (masked by TTS playback); if a live smoke shows a demo-noticeable gap on rapid same-speaker multi-utterance, the BE Option-B continuous-orchestrator is the orch's to scope.

## TDD compliance
**Clean.** 080/081/082 were RED-first (080: 10 RED; 081: 2 import-not-found + 2 assertion RED; 082: 3 re-arm RED). 085's Model fix + 2b relabel were RED-first. The two **characterization** slices (084 in full; the cost-half of 085) were green-at-Step-3 and **explicitly orchestrator-authorized** (the underlying behavior was already correct) — reported honestly at Step 3 each time; not violations.

## Reachability (Step 7.5 — all confirmed)
- **080** — toggle → `SessionSetup` (rendered); cascade flag → `recordingController` singleton → `buildStartFrame` → WS; `{type:"direction"}` → `cascadeStreamClient` singleton → dispatch → `setTurnDirection`; realtime mint → `realtimeWebRtcClient` singleton; realtime heuristic → `realtimeTurnController` singleton (manual + auto).
- **081** — `App.tsx:136 <TranscriptPanel>` → `TurnCard` → `DirectionBadge`.
- **082** — `main.tsx:18 setOnTerminal(…onCascadeTerminal())` (the re-arm trigger) + `RecordingControls` Start/Stop.
- **084** — characterizes the existing reachable continuous-loop path (no new code).
- **085** — `ComparisonSummary` ← `App.tsx:145`; `SessionDetail` ← `SessionHistory.tsx:145`.
No tested-but-unwired gaps.

## Open follow-ups (Step-9 categorized — for the orchestrator to verify at `/orchestrate-end`)
- **Cross-doc invariant change (080) → ARCHITECTURE.md + web/CLAUDE.md (orchestrator-written hot):** the 3 TS wire-mirrors — cascade start `bidirectional` (ARCH-009/011), the `{type:"direction"}` cascade message (ARCH-009/011), `RealtimeTokenRequest.bidirectional` (ARCH-009/010). (`UiSessionState.bidirectional` + `setTurnDirection` are FE-runtime, not wire mirrors.)
- **Convention candidate (web LESSONS, orchestrator-written):** §32 covers the direction-attribution asymmetry + omit-when-false flag (080); §33 the cascade-continuous pattern (082/084); §34 the model-from-`providerProfile`-per-mode + consistent single-model relabel (085).
- **Architecture-doc note (082) → ARCH-003/011 (orchestrator-written):** cascade auto-VAD is now *continuous* (completes the I.3 single-turn version).
- **Future TODO — Carry-forward / deferred-hardening (orchestrator-routed):** BE finalize-pending-turns-on-`/end` (084 orphan); `DirectionBadge` in `SessionDetail` (081 consistency); Stop="End conversation" label in auto (082 UX polish); dispatcher-wide WS cast-hardening + `detectLanguage` length-cap (080 security LOW/MED); the inter-turn gap live-smoke (082, capture-gated).
- **Resolved:** the 085 cost-diagnosis residual (the lead's JSON confirmed the per-turn cost is present + already displays — no follow-up).

## Slice commits this session
080 `52ee24f` · 081 `62022f9` · 082 `619e3f9` · 084 `5f46009` · 085 `bc25c88`. Full web suite **342 green**; `tsc --noEmit` + `eslint src` clean throughout.

## Preflight (close-out gate)
`/preflight` (web) ran clean on lint ✓ / typecheck ✓ / test ✓ (342) / build ✓ — but **`format:check` (Prettier) flagged 4 slice files** (`detectLanguage.ts`, `cascadeStreamClient.test.ts`, `SessionDetail.test.tsx`, `TranscriptPanel.test.tsx`). **Root:** the per-slice `/tdd` Step-8 web gate is `typecheck && lint` only — it does NOT include `format:check`, so Prettier drift accrued unnoticed across the round. **Resolved:** `prettier --write` on exactly those 4 files (whitespace-only; 342 still green) → `format:check` clean, committed as a `style(web)` commit. **Process note for the orchestrator/future-FE:** consider adding `npm run format:check` to the per-slice web Step-8 (or a pre-commit) so format drift is caught per-slice, not only at `/preflight`.
