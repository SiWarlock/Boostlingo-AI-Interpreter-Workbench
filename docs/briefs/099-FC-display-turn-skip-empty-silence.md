# /tdd brief — display_turn_skip_empty_silence

## Feature
Fix the per-turn Latency + Cost cards flipping to N/A when a trailing empty auto-VAD turn arrives (user Finding C): introduce ONE shared `selectDisplayTurn(state)` selector that both `MetricsPanel` and `CostPanel` use, which skips trailing empty-silence turns so the panels keep showing the last MEANINGFUL turn's metrics instead of an empty turn's n/a.

## Use case + traceability
- **Task ID:** Finding-C (user live-test 2026-06-01; cascade + bidirectional + AutoVAD: a good turn shows the summary, then a spurious empty-transcription turn flips the per-turn cards to N/A)
- **Architecture sections it implements:** `ARCH-007` (frontend state/component separation, selectors), `ARCH-013` (metrics display)
- **Related context:** the 3rd empty-turn bug in this area (052 false-fail, §39/083 + 097 summary exclusion, now C) — this is the FE-display sibling of the backend §39/097 exclusion. **Confirmed (§34) against the user's real JSON:** the BACKEND summary is CORRECT (`session_20260601T172021Z_37a0c7c6.json`: cascade turnCount=2, cpm 0.0087 — the empty turn is correctly excluded by `IsEmptySilence`); the flip is purely the FE's per-current-turn display. **NOT a 094/095/097 regression** — those are backend; the per-turn-display selection has always landed on the latest turn. web LESSONS §25 (deriveTurnMetrics per-turn), §13.

## Root cause (confirmed)
`MetricsPanel.tsx:80` and `CostPanel.tsx:16` both compute `const turn = state.currentTurn ?? state.turns[state.turns.length - 1]` (the SAME expression, duplicated). Timeline: a good turn completes → moves into `turns[]`, `currentTurn=undefined` → panels show `turns[last]` = the good turn (correct). Auto-VAD re-arms → a spurious empty turn records → auto-finalizes (0 transcripts, no cost) → `completeTurn` moves it into `turns[]` as the NEW last element → panels fall back to `turns[last]` = the empty turn → `deriveTurnMetrics` finds no `tts.first_audio`/stage markers/cost → all per-turn Latency + Cost values render **n/a**, persistently. The Session-averages rows (`state.summary`) + ComparisonSummary (backend summary) are NOT affected (the user's "summary flips to N/A" is specifically the per-turn Latency + Cost cards).

## Acceptance criteria (what "done" means)
- [ ] A single shared selector `selectDisplayTurn(state): TurnViewModel | undefined` in `web/src/state/selectors.ts` is the ONE source for "which turn the per-turn panels display."
- [ ] `selectDisplayTurn` returns the **last MEANINGFUL turn**, skipping trailing empty-silence turns. Empty-silence (FE-side, mirroring the backend `IsEmptySilence` notion): `sourceTranscript.length===0 && targetTranscript.length===0 && cost == null` (confirm the exact `TurnViewModel` field names at Step 1 — the FE view-model uses `sourceTranscript`/`targetTranscript`/`cost`, NOT the persisted `transcripts`/`costEstimate`).
- [ ] Selection rule (default; refine at Step 2.5): prefer `currentTurn` when it is non-empty; else the most recent non-empty turn in `turns[]`; else (no non-empty turn anywhere) fall back to `currentTurn ?? turns[last]` (preserve today's behavior so a brand-new session / a genuinely-empty first turn still renders its in-progress/empty state — never crash, never blank when there's nothing better).
- [ ] `MetricsPanel` + `CostPanel` both call `selectDisplayTurn(state)` (replace the duplicated `currentTurn ?? turns[last]` line in BOTH).
- [ ] After a good turn, a trailing empty auto-VAD turn does NOT blank the per-turn Latency/Cost cards — they keep showing the good turn's metrics. (The core Finding-C fix.)
- [ ] An in-progress good turn still displays live as it fills (the selector must not "stick" on a prior turn once the current turn has content).
- [ ] Session-averages rows + ComparisonSummary behavior UNCHANGED (they read `state.summary`, not the display turn).
- [ ] All unit tests for `selectDisplayTurn` pass; the jsdom MetricsPanel/CostPanel tests cover the trailing-empty-turn case (good turn stays displayed).
- [ ] `/preflight` clean.

## Wiring / entry point (Step 7.5)
`MetricsPanel` + `CostPanel` (rendered in `App.tsx`) → `selectDisplayTurn(useSessionState())` → `deriveTurnMetrics` / `turn.cost`. Confirm both panels route through the new selector (not the old inline expression) — grep that the duplicated `currentTurn ?? turns[length-1]` line is gone from both.

## Files expected to touch
**Modified:**
- `web/src/state/selectors.ts` — add `selectDisplayTurn` (pure; the empty-silence predicate + the skip-trailing-empty selection).
- `web/src/components/MetricsPanel.tsx` — use `selectDisplayTurn`.
- `web/src/components/CostPanel.tsx` — use `selectDisplayTurn`.
- `web/src/state/selectors.test.ts` (+ the panel jsdom tests) — `selectDisplayTurn` unit tests + the panel trailing-empty render tests.

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2) — `selectors.test.ts`
1. **`selectDisplayTurn_skips_trailing_empty_silence_turn`** — `turns=[goodTurn, emptySilenceTurn]`, `currentTurn=undefined` → returns `goodTurn` (NOT the empty one). Why: the Finding-C core.
2. **`selectDisplayTurn_prefers_nonempty_current_turn`** — `currentTurn=goodInProgress` (has transcripts) → returns `currentTurn`. Why: live in-progress display preserved.
3. **`selectDisplayTurn_empty_current_turn_falls_back_to_last_meaningful`** — `currentTurn=emptyInProgress` (just re-armed, no content yet), `turns=[goodTurn]` → returns `goodTurn` (keeps the good metrics during the empty recording window). Why: the no-flip-during-silence-window behavior.
4. **`selectDisplayTurn_no_meaningful_turn_preserves_today_behavior`** — only an empty turn exists (new session / genuine first-turn silence) → returns that turn (or undefined per today), never crash. Why: don't regress the empty-state render.
5. **(panel jsdom)** MetricsPanel + CostPanel: after `[good, emptySilence]` with no currentTurn, the headline/cost show the GOOD turn's values, not n/a.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none (pure selector + component wiring; no model/wire shape change).
- **Orchestrator doc rows to write hot:** none structural. A web LESSONS candidate (the FE-display sibling of §39/097 — skip trailing empty-silence in the per-turn display) — I write it at Step 9.

## Things to flag at Step 2.5
1. **Empty-silence predicate fields.** Confirm the `TurnViewModel` empty check: `sourceTranscript.length===0 && targetTranscript.length===0 && cost == null` (vs any other "no content" signal — e.g. a status field). My default vote: **transcripts-empty + cost-null** (mirrors the backend `IsEmptySilence` semantics on the view-model's fields). Confirm the exact field names at Step 1.
2. **Keep-last-meaningful (A) vs explicit "silence — no metrics" placeholder (B).** My default vote: **(A) keep showing the last meaningful turn** — it's exactly what the user wants (the good metrics shouldn't vanish on a spurious silence turn); a placeholder (B) would still remove the good turn's numbers. (A) is the natural fix; the lead can redirect to (B) if the user prefers an explicit indicator. Lean strongly: A.
3. **In-progress empty current turn.** During the empty turn's recording window `currentTurn` is empty → the selector falls back to the last meaningful turn (so the display doesn't flicker to n/a mid-silence). Acceptable that, for the first instant of a NEW GOOD turn's recording (before transcripts arrive), it briefly shows the prior turn until the new transcripts land? My default vote: **yes, acceptable** (it updates the moment the new turn has content; far better than a persistent n/a). Flag if you want a "currentTurn is recording" override.
4. **Selector home + naming.** `selectDisplayTurn` in `selectors.ts` (alongside `deriveTurnMetrics`). My default vote: **yes** — it's a pure store-read selector, the natural home; both panels import it. Confirm no circular-import issue.

## Dependencies + sequencing
- **Depends on:** nothing (independent of 097's backend fix — this is the FE-display sibling).
- **Blocks:** nothing; closes Finding C (the cascade per-turn cards stay meaningful in continuous/auto-VAD).

## Estimated commit count
**1.** A shared selector + two one-line panel swaps + tests. Not a safety slice; reviewer fan-out disabled. (This is the UNIFIED fix the lead asked about — one selector mirroring the backend empty-silence notion, replacing the duplicated per-panel expression; no whack-a-mole.)

## Lessons-logged candidates anticipated
- **Convention candidate** — "the per-turn display must select the last MEANINGFUL turn via one shared `selectDisplayTurn` (skip trailing empty-silence turns: 0 transcripts + null cost) — the FE-display sibling of the backend `IsEmptySilence` (§39/097); a raw `currentTurn ?? turns[last]` lands on a spurious auto-VAD silence turn and blanks the good metrics. Mirror the backend empty-silence notion on the view-model's fields, in ONE place both panels share."
- **Architecture-doc note candidate** — ARCH-013: per-turn panels display the last meaningful turn (auto-VAD trailing silence turns are skipped in the display, as they are in the summary).

## How to invoke
> Don't prescribe `/session-start` (the FE session is oriented). Jump to pre-flight + `/tdd`.
1. Read this brief end-to-end (incl. Step 2.5 questions).
2. Run `/tdd display_turn_skip_empty_silence` in the frontend (`web/`).
3. Step 0 restate → Step 1 file list (confirm the `TurnViewModel` empty-check fields + both panels' shared expression) → Step 2 RED → **Step 2.5 ping the orchestrator** with test designs + answers to the 4 questions → GREEN after approval.
4. Step 9: surface the lesson candidate + the draft commit message.
