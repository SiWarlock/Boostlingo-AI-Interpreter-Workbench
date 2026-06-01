# /tdd brief — history_detail_display_fixes

## Feature
Fix the Phase-J smoke display bugs in the session-history drill-in + comparison panel:
- **(2a) SessionDetail Model = n/a for realtime** — read the model per mode from session config (like ComparisonSummary), not the cascade-only per-turn field.
- **(2a) SessionDetail Cost = n/a for cascade** — diagnose + fix (the session-level cascade cost is present; the drill-in shows n/a).
- **(2b) Comparison panel realtime stage rows** — relabel realtime STT/Translation/TTS-final from three `n/a` rows to a single-model note (mirror 074's MetricsPanel relabel).

## Use case + traceability
- **Task ID:** J.7 (Phase-J smoke Finding 2a/2b — display polish)
- **Architecture sections it implements:** ARCH-007 (history/comparison display), ARCH-013 (metrics labels)
- **Related context:** Phase-J smoke (Finding 2). Session `data/sessions/session_20260601T025341Z_ed23aff3.json`. **074** (`6892430`) relabeled the *MetricsPanel* realtime stages (single-model note vs three `n/a`) — do the same for the *comparison* panel. ComparisonSummary already reads the model correctly from session config (`comparisonActions.ts:66-70`).

## ⭐ Roots (orchestrator-confirmed)
- **2a Model:** `SessionDetail.tsx:~127` renders `v.translationModelUsed ?? 'n/a'` — `translationModelUsed` is **cascade-only** (null on realtime turns) → realtime shows n/a. ComparisonSummary instead reads `session.config.providerProfile.{translationModel,realtimeModel}` per mode → correct.
- **2a Cost:** `SessionDetail.tsx:~130` renders `formatCostPerMinute(v.cost)` (per-turn `costEstimate`). The lead reports cascade Cost=n/a while `summary.cascade.estimatedCostPerMinuteUsd`=0.0106 is present → **diagnose against the session JSON**: do the SUCCESSFUL cascade turns carry a per-turn `costEstimate`? If yes → the drill-in reads the wrong field/path; if the per-turn cost is only on some turns → fall back to the session-summary cost where the per-turn is null. (A genuinely failed turn legitimately has null cost — that n/a is honest; don't fabricate.)
- **2b:** the comparison panel renders three realtime stage rows as `n/a`; 074 already solved this for MetricsPanel (a "single model — no discrete stages" note). Reuse that treatment.

## Acceptance criteria
- [ ] SessionDetail shows the correct **Model** per turn: realtime → `session.config.providerProfile.realtimeModel`; cascade → `translationModelUsed ?? session.config.providerProfile.translationModel`. No n/a when the model is knowable from config.
- [ ] SessionDetail **Cost**: a successful cascade turn shows its cost (per-turn `costEstimate`, or the session-summary `$/min` fallback where per-turn is absent) — diagnosed against the real session JSON; a genuinely failed/costless turn stays honest n/a (never fabricated).
- [ ] The comparison panel's realtime stage rows render a **single-model note** (mirroring 074) instead of three bare `n/a` rows; cascade stages unchanged.
- [ ] Vitest units pass; `tsc`/eslint/`/preflight` clean.

## Files expected to touch
**Modified:**
- `web/src/components/SessionDetail.tsx` — Model per-mode read (+ the session-config source); Cost field fix.
- `web/src/state/historyDetail.ts` (if the model/cost mapping needs the session-config fields threaded into the view model) — confirm at Step 1.
- The comparison panel component (find — `ComparisonSummary`/`ComparisonView`) — the realtime stage-row relabel (reuse 074's MetricsPanel pattern).
- The matching test files.

## RED test outline (Step 2)
1. **`session_detail_realtime_shows_realtime_model`** — a realtime turn in a session with `providerProfile.realtimeModel='gpt-realtime'` ⇒ Model renders "gpt-realtime", not n/a. Why: 2a model.
2. **`session_detail_cascade_shows_translation_model`** — a cascade turn ⇒ Model = `translationModelUsed` (or the config fallback), not n/a. Why: 2a model.
3. **`session_detail_successful_cascade_shows_cost`** — a completed cascade turn with a cost ⇒ Cost renders the value (not n/a). Why: 2a cost (pin the diagnosed fix).
4. **`session_detail_failed_turn_cost_honest_na`** — a genuinely costless turn ⇒ n/a (not fabricated). Why: honesty preserved.
5. **`comparison_realtime_stages_single_model_note`** — the comparison panel renders the single-model note for realtime stages, not three n/a rows. Why: 2b.

## Cross-doc invariant impact
- **Model field changes:** none (display reads existing fields). No cross-doc.

## Things to flag at Step 2.5
1. **2a Cost diagnosis** — read the actual session JSON (`data/sessions/session_20260601T025341Z_ed23aff3.json`): do the successful cascade turns carry a per-turn `costEstimate`? **Default vote: fix the drill-in to show the per-turn cost where present; fall back to the session-summary `$/min` only if the per-turn is structurally absent** — report the finding at Step-2.5 so the fix matches reality (don't guess).
2. **2b — relabel vs accept** — single-model note (mirror 074) vs leave as genuine n/a? **Default vote: relabel** (consistency with the MetricsPanel; realtime genuinely has no discrete stages, so three n/a rows misread as "broken").
3. **Model source threading** — does SessionDetail already receive the full `InterpretationSession` (with `config.providerProfile`), or only a per-turn view model? **Default vote: thread the session config in** (it's the authoritative model source, as ComparisonSummary uses).

## Dependencies + sequencing
- **Depends on:** nothing blocking (independent of Finding 1). Can run after 084.
- **Blocks:** a clean history drill-in for the demo.

## Estimated commit count
**1–2.** 2a (SessionDetail model+cost) + 2b (comparison relabel) — bundle if tidy, or split 2a/2b. FE-only, no safety invariant.

## Lessons-logged candidates anticipated
- **Convention candidate** — display the model/identity from the authoritative session-config `providerProfile` (per mode), NOT a cascade-only per-turn field; relabel single-model (realtime) stage rows consistently across MetricsPanel + ComparisonSummary + SessionDetail (no three-bare-n/a).
