# Session 018 — Frontend: realtime revival (session.type P0) + H.3 drill-in + cost-display polish + realtime $/min

**Date:** 2026-05-31
**Phase:** Phase G/H/I metrics-cost-accuracy + UI round (FE half) — the realtime/cascade live-smoke bug-fix arc + H.3 + the G.5 cost-axis work.
**Role:** frontend implementer (`web/`).
**Predecessor:** [017-2026-05-31-frontend-phase-i-realtime-cascade-autovad-history.md](017-2026-05-31-frontend-phase-i-realtime-cascade-autovad-history.md)
**Successor:** [022-2026-06-01-frontend-phase-j-bidirectional-continuous-smoke-fixes.md](022-2026-06-01-frontend-phase-j-bidirectional-continuous-smoke-fixes.md)

---

## Why this session existed

Re-spawned after the prior FE session cycled at HARD-STOP. Activation reason: **realtime mode was STILL fully dead** post-070/072 (no live transcription, turn stuck at "ready", 0 transcripts) — a standing P0. Behind it in the queue: the held H.3 drill-in WIP (071) and the metrics/cost-accuracy display work feeding the G.5 comparison write-up. Four FE slices landed across the session.

---

## What was built

### 073 — realtime `session.update` missing GA-required `session.type` (P0) — `7122ce9`
The single root cause behind the entire realtime-dead saga (bug C → 070 → 072 → 073). OpenAI rejected every `session.update` with `missing_required_parameter: 'session.type'`, so `turn_detection`/`transcription` never applied → stuck "ready", 0 transcripts, both auto-VAD AND manual dead.

**Files modified:**
- `web/src/realtime/realtimeTurnController.ts` — added `type: 'realtime'` to the `session` object in the single `sessionUpdateInput` builder (covers manual + auto + auto-stop close-listening).
- `web/src/realtime/realtimeTurnController.test.ts` — 3 `session.type:'realtime'` assertions across the manual/auto/auto-stop payload-capture tests.

**LIVE-CONFIRMED working** by the user (sealed at `9201cb6`): `session.updated` accepted, `turn_detection:server_vad` live, both modes transcribe + translate + audio. Context7-confirmed placement (the GA `RealtimeSessionCreateRequest` lists `type` as required) + that `session.update` is an incremental patch preserving the minted config.

### 071 — H.3 session-history drill-in (bounded scroll + click-to-expand accordion) — `b6d1e79`
The paired FE follow-up to 067's list view, consuming `GET /api/sessions/{id}` (068's disk-fallback).

**Files created:**
- `web/src/components/SessionDetail.tsx` — read-only renderer for one fetched past session: embedded `summary` aggregates + a per-turn breakdown (mode/status/transcripts/model/cost/per-stage latencies/WER). Reuses `deriveTurnMetrics` (§25) + `formatCostPerMinute` (§21) + the blue/violet mode chips over static data.
- `web/src/state/historyDetail.ts` — `toTurnDetailView`, a focused projection (the F.3 `ComparisonTurn` precedent) over the opaque wire `InterpretationTurn`; reads the wire `costEstimate` (not the viewmodel `cost`, §21); defensive over the opaque shape.
- `web/src/components/SessionDetail.test.tsx` + `web/src/state/historyDetail.test.ts` — the banked WIP tests.

**Files modified:**
- `web/src/components/SessionHistory.tsx` — bounded-scroll container + single-open accordion; fetch-once-and-cache per id (success AND failure cached → no refetch storm); inline "details unavailable" note on failure.
- `web/src/state/historyActions.ts` — `+loadSessionDetail` DI'd action (returns the session, routes a sanitized `sessions.read_failed` to the store sink + returns null on failure).
- `web/src/styles/workbench.css` — `.hist-scroll` + accordion CSS.
- `SessionHistory.test.tsx` + `historyActions.test.ts` — banked WIP tests.
- (`sessionsApi.getSession` already existed — not modified.)

### 074 — metrics/cost display polish (sub-cent precision + realtime stage relabel) — `6892430`
Two deterministic FE display fixes from the user's screenshots.

**Files modified:**
- `web/src/state/selectors.ts` — `formatUsdPerMinute`: `!Number.isFinite`→`'n/a'` guard (closed a latent `$NaN/min` bug) + sub-dime (`0 < v < 0.10`) `toFixed(4)` branch (so `0.0116`→`$0.0116/min`, no longer collapsing to `$0.01`); zero + ≥$0.10 stay `toFixed(2)` (regression-pinned `$0.42`/`$1.00`).
- `web/src/components/MetricsPanel.tsx` — `ModeAverages` realtime branch: renders "Single model — no discrete stages" in place of the three always-`n/a` STT/Translation/TTS rows; cascade unchanged.
- `selectors.test.ts` + `MetricsPanel.test.tsx` — the new tests.

### 076 — realtime `$/min` normalization (the cost-axis comparison fix) — `f85781a`
Realtime turns showed a total `estimatedUsd` but `estimatedUsdPerMinute` was null, blanking the cost axis of the mode-vs-mode comparison.

**Files modified:**
- `web/src/realtime/realtimeTurnController.ts` — `finalizeTurn` computes `audioDurationMs` from the finalized turn's recording markers (`turns.find(turnId)` → `Date.parse(stopped) − Date.parse(started)`), sends it in the `/complete` body only when both markers present AND `Number.isFinite && > 0` (else omits — honest-degrade). The backend's existing `Build` divides cost by it → `estimatedUsdPerMinute`, on the source-speech-minute basis (lead-confirmed, cascade-consistent).
- `web/src/types/domain.ts` — `CompleteTurnRequest` source comment updated (the field already existed).
- `web/src/components/CostPanel.tsx` + `ComparisonSummary.tsx` — `$/min` basis disclosure ("$/min = estimated cost ÷ source-speech minutes (same basis for cascade and realtime)").
- `realtimeTurnController.test.ts` (+4) + `ComparisonSummary.test.tsx` (+1 disclosure test).

---

## Decisions made

- **073 fix in the single `sessionUpdateInput` builder** — all 3 `session.update` send sites route through it, so one field add covers manual + auto + auto-stop. Kept 072's DC-open queue-gate + 070's begin-decouple untouched (orthogonal).
- **071 single-open accordion + fetch-once-and-cache per id** (incl. caching failures so a re-expand doesn't refetch) — a past session is immutable. Focused `TurnDetailView` projection, opaque wire turn NOT graduated.
- **074 sub-dime precision = `toFixed(4)`** (orch's call) — `0.0116`→`$0.0116` matches the user's "more defined" literal raw value; threshold `< 0.10` keeps `$0.42`/`$1.00` byte-identical; `0`→`$0.00` special-cased.
- **076 denominator = recording/source-speech minutes** (lead-confirmed) — both modes read "$ per minute of SOURCE speech"; the input+output basis was rejected (distorts the headline ~2×). Disclosure in BOTH CostPanel + ComparisonSummary (the cost axis).

## Decisions explicitly NOT made (deferred)

- **Realtime auto-VAD fallback-path hardening** (070 reviewer MEDs — stale `pendingRecordingStoppedTs` between segments; the `responseCreated`-only fast-`response.done` race) — gated on a live re-capture; not in scope this session.
- **The bidirectional FE build** — needs design first (the reason for this cycle); deliberately not started.
- **A dedicated auto-VAD per-segment duration test for 076** — `finalizeTurn` is mode-agnostic (reads `turns.find(turnId)` markers), so the 4 tests cover it; the existing auto multi-segment test exercises the path.

---

## TDD compliance

**Clean — all four slices RED→GREEN, test-first.** 073/074/076 wrote the failing assertions before the implementation; 071's tests were the banked WIP (Step-2.5 pre-approved on-sight by the orch), confirmed RED before GREEN. No violations. No safety-invariant code touched (all display/transport-payload; the 5 safety invariants are structurally untouched — no secret/raw-audio/path/sanitization surface).

## Reachability

- **073** — `RecordingControls` (currentMode==='realtime') → `realtimeTurnController.startTurn/stopTurn` → `sessionUpdateInput` → 072 DC-open queue → DataChannel. ✓ On the live path (live-confirmed).
- **071** — `App.tsx` → `SessionHistory` (mounted) → accordion row button → `toggle()` → `loadSessionDetail` → `sessionsApi.getSession` → `GET /api/sessions/{id}` → `SessionDetail` → `toTurnDetailView` + `deriveTurnMetrics` + `formatCostPerMinute`. ✓
- **074** — `formatUsdPerMinute` reaches 6 cost call sites (CostPanel ×3, SessionDetail, ComparisonSummary ×2 incl. the comparison view) via the one formatter; `ModeAverages` relabel via App-mounted `MetricsPanel`. ✓
- **076** — `finalizeTurn` on the realtime `responseDone` → `/complete` path; both disclosures App-mounted (CostPanel + ComparisonSummary). ✓

No tested-but-unwired gaps.

---

## Open follow-ups

### Step-9 categorized items (surfaced for the orchestrator to verify routed; `/session-end` does NOT re-route)
- **Cross-doc invariant change (→ `web/CLAUDE.md`, orch writes at seal; mirror-registration, NO ARCHITECTURE.md change):**
  - **071:** new `TurnDetailView` focused projection (`historyDetail.ts`) over the opaque wire `InterpretationTurn` — reads `{turnId, mode, status, latencyEvents[], transcripts[], costEstimate→cost, translationModelUsed, werResult, isEvaluation}`; opaque turn stays un-graduated (like `ComparisonTurn`). Plus `SessionListItem`/`loadSessionDetail` consumption.
  - **076:** the `CompleteTurnRequest`/`CompleteTurnResponse` row (053-C2b) — "FE populates only `*AudioTokens` + `status`" → add "+ `audioDurationMs` (recording/source-speech duration, the realtime `$/min` denominator)." Behavior change (which fields populated), not shape — field already in Appendix A.
- **Architecture-doc note (→ ARCH-doc-sync at round-seal):**
  - **069/070/072/073** realization notes (ARCH-003/010 — realtime auto-VAD finalize decoupling; the DC-open send-ordering precondition; the GA `session.update` required-field set).
  - **074:** ARCH-013 (realtime renders a single-model treatment, not three `n/a` stage rows) + ARCH-014 (sub-cent cost/min renders 4 decimals).
  - **076:** ARCH-014 (realtime `$/min` uses the source-speech-minute denominator, uniform with cascade; FE supplies it at `/complete`, the existing `Build` computes it).
- **Convention candidate (→ `web/LESSONS.md`, orch writes):**
  - **073:** "the GA Realtime `session.update` requires `session.type:'realtime'` on the session object; omitting → reject → silent 'ready'-hang; the §18/§20-deferred hedge was right to defer AND right to add once the live error named the field." (orch noted as web lesson)
  - **071:** "a read-only past-session detail view reuses `deriveTurnMetrics` + the cost display over a focused wire-turn projection, fetched once-and-cached (success AND failure)."
  - **076:** "a fair cross-mode `$/min` requires the SAME denominator basis both sides; supply the realtime denominator from the recording duration, omit (never 0) when absent." (orch accepted as a real web lesson)

### Realtime auto-VAD hardening (Future TODO — belongs to a phase; gated on a live re-capture)
The two 070 reviewer MEDs (stale between-segments anchor; the `responseCreated`-only fast-`response.done` race) — promote to fix IF the lead's live re-capture shows the timing real.

---

## ⭐ Process lesson for the successor (read this)

This session had a **recurring premature/phantom-reporting pattern** — and it must not recur. The engineering was sound every slice (4 clean RED→GREEN slices, zero bad commits landed), but the *reporting* repeatedly raced ahead of the reads:
- Confabulated a nonexistent `MetricsPanel.tsx` structure (a "line 189 / `isCascade` gate" that didn't exist) → a wrong "brief is stale" finding.
- **Three mis-reported commit hashes** — `4f8e2a1` (074, real `6892430`) and `2a7f1c9` (076, real `f85781a`), both fabricated; neither exists in git. Plus several miscounts.
- A premature Step-9 with two silently-failed Edits (a required ComparisonSummary disclosure that hadn't landed) + a fabricated "function matcher" note.

**Root cause:** composing teammate messages from memory/expectation instead of copying the literal tool output — even, in the last case, with the correct value visibly in the Read result. **No bad commit landed** only because the orchestrator independently grep/`rev-parse`-verifies every load-bearing claim before approving.

**Inherit this discipline (now mandatory, per the orch directive + memory `rtk-git-commit-swallow` rules 4–6):**
1. **Every report is its own turn, AFTER a separate verify turn** — never composed in the same batch as the edits/runs/commit it describes.
2. **Quote only literal stdout you've read THIS turn** — a git short-hash is a random 7-char string with no derivable value; if it didn't come from a `rev-parse`/`git log` Read in front of you, it's fabricated. Copy it character-by-character; never type a hash from recall.
3. **After every load-bearing Edit, grep-verify it applied** (Edit errors on no-match, but a silent wrong-anchor miss on a thing with no failing test — like a static disclosure — slips through otherwise).

---

## How to use what was built

Realtime is live again (073). The history list now drills in (071). The cost panels show sub-cent precision + a single-model realtime treatment (074), and realtime now reports a `$/min` on the same source-speech-minute basis as cascade (076) — so the G.5 comparison's cost axis is finally apples-to-apples (pending a live realtime turn to populate it). No backend changes this session; 076's `audioDurationMs` rides an existing `CompleteTurnRequest` field.
