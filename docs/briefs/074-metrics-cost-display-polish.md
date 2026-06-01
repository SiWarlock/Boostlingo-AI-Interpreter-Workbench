# /tdd brief — metrics_cost_display_polish

## Feature
Two small, deterministic FE display fixes surfaced by the user's live metrics/cost screenshots:
**(1)** the cost-per-minute formatter renders real decimals for sub-cent values (today `$0.0116/min` shows as `$0.01/min`); **(2)** the session-averages panel relabels realtime's STT/Translation/TTS stage rows from the misleading `n/a` to a "single model — no discrete stages" treatment (realtime is by-design stage-less, not missing data).

## Use case + traceability
- **Task ID:** metrics/cost-accuracy round (feeds G.5 write-up) — FE half.
- **Architecture sections it implements:** `ARCHITECTURE.md` ARCH-014 (estimated cost/min display), ARCH-013 (latency/metrics panel; realtime has no discrete stages).
- **Related context:** Carry-forward "Cost-precision (FE slice 073→074, LOW)" + the user's screenshot findings relayed by the lead. web LESSONS §21 (cost display, `costEstimate`≠`cost`), §22 (styling slice queries role/aria-label/text, never className), §14 (jsdom component tests). The realtime $/min *normalization* (sending `audioDurationMs` at `/complete`) is a SEPARATE slice (075) — this brief is display-only and does NOT touch the realtime turn controller or any wire payload.

## Acceptance criteria (what "done" means)
**Feature 1 — cost-precision:**
- [ ] `formatUsdPerMinute` (the single shared formatter in `web/src/state/selectors.ts`; `formatCostPerMinute` delegates to it) renders ≥2 significant figures for sub-dime values so a raw `0.0116` no longer collapses to `$0.01/min`.
- [ ] Values ≥ `$0.10` are UNCHANGED (still 2-decimal cents) — existing assertions (`$0.42/min`, `$1.00/min`) stay green.
- [ ] `null` / `undefined` still → the shared `'n/a'` token (never a bare 0, never an error). The `Estimated $…/min` qualifier is preserved.
- [ ] Both per-turn cost (CostPanel via `formatCostPerMinute`) and per-mode session figures (`ModeSummary.estimatedCostPerMinuteUsd` via `formatUsdPerMinute`) inherit the fix from the one formatter.

**Feature 2 — realtime stage relabel:**
- [ ] In `MetricsPanel`'s `ModeAverages`, the realtime block renders a "single model — no discrete stages" treatment in place of the three per-stage rows (`Avg STT final` / `Avg translation final` / `Avg TTS first audio`) — NOT three `n/a` rows.
- [ ] The cascade block is UNCHANGED (still shows the three per-stage averages with values).
- [ ] Realtime STILL renders the rows that DO apply to it: `Avg speech-end → first audio`, `Avg speech-end → playback`, `Errors`, and the `(N turns)` eyebrow.
- [ ] `/preflight` clean.

## Files expected to touch
**Modified:**
- `web/src/state/selectors.ts` — `formatUsdPerMinute` adaptive sub-cent precision (Feature 1).
- `web/src/state/selectors.test.ts` — extend the existing `formatCostPerMinute`/`formatUsdPerMinute` describe block with sub-cent cases (Feature 1).
- `web/src/components/MetricsPanel.tsx` — `ModeAverages` realtime stage-row relabel (Feature 2).
- `web/src/components/MetricsPanel.test.tsx` — new realtime-vs-cascade `ModeAverages` assertions (Feature 2).

If implementation needs files beyond this list, **flag at Step 2.5** before going GREEN.

## RED test outline (Step 2)

**Feature 1 — `web/src/state/selectors.test.ts` (extend the `formatCostPerMinute`/`formatUsdPerMinute` block):**
1. **`sub_cent_renders_significant_figures`** — `formatUsdPerMinute(0.0116)` renders more than 2 decimals (≥2 sig figs), NOT `$0.01/min`. Exact expected string resolves with the Step-2.5 formula choice below.
   - Asserts: the rendered string conveys the sub-cent magnitude (e.g. `$0.012/min` for the ≥2-sig-fig lean, or `$0.0116/min` for the fixed-4-decimal alternative).
   - Why: ARCH-014 — the estimate carries full precision; the display was erasing the sub-cent signal (user finding).
2. **`at_or_above_a_dime_unchanged`** — `formatUsdPerMinute(0.42)` → `Estimated $0.42/min`, `formatUsdPerMinute(1)` → `Estimated $1.00/min` (existing assertions hold).
   - Asserts: ≥$0.10 path is byte-identical to today.
   - Why: regression guard — the fix is sub-dime-only.
3. **`null_and_undefined_still_na`** — `formatUsdPerMinute(null)` / `(undefined)` → `'n/a'`.
   - Why: the never-0/never-error rule (web §2 single error sink; lesson §21).
4. **`tiny_value_floor`** — a very small value (e.g. `0.0009`) renders ≥2 sig figs (e.g. `$0.00090/min`), not `$0.00/min`.
   - Why: pins the sig-fig behavior at the extreme; guards against a `$0.00` synthetic-zero read.

**Feature 2 — `web/src/components/MetricsPanel.test.tsx` (jsdom; per-file `// @vitest-environment jsdom` + `cleanup`, lesson §14):**
5. **`realtime_averages_relabel_stages`** — render MetricsPanel with a `summary.realtime` ModeSummary present; the `realtime-averages` region shows the single-model indicator and does NOT render three `n/a` stage rows for STT/Translation/TTS.
   - Asserts (query by aria-label/text per §22, never className): `realtime-averages` contains the single-model treatment text; it does NOT contain the literal `Avg STT final` … `n/a` stage rows.
   - Why: ARCH-013 — realtime has no discrete stages; `n/a` mis-reads as missing data.
6. **`cascade_averages_unchanged`** — with a `summary.cascade` ModeSummary present, the `cascade-averages` region STILL renders `Avg STT final` / `Avg translation final` / `Avg TTS first audio`.
   - Why: regression guard — the relabel is realtime-only.
7. **`realtime_keeps_applicable_rows`** — the realtime block still renders `Avg speech-end → first audio`, `Avg speech-end → playback`, and `Errors`.
   - Why: only the stage-specific rows are relabeled; the responsiveness/error rows apply to both modes.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** NONE. No `domain.ts` model touched; no wire payload changed; no new contract field. (`ModeSummary` is read, not modified.)
- **Orchestrator doc rows to write hot:** none (no cross-doc table row, no Appendix A change). Possible ARCH-013/014 realization-note prose (the realtime-stage-relabel + sub-cent display convention) — orchestrator folds at round-seal if warranted.

## Things to flag at Step 2.5
1. **Cost-precision formula — exactly how many figures for sub-cent?** Options: **(a)** adaptive ≥2 significant figures (floor 2 decimals) → `0.0116`→`$0.012/min`, `0.0009`→`$0.00090/min` [**my default vote** — guarantees real signal, compact]; **(b)** fixed 4-decimal for sub-dime → `0.0116`→`$0.0116/min` (shows the raw figure literally, but coarse values get trailing zeros); **(c)** per-hour secondary (`$0.70/hr`). The user's literal ask = "more defined than nearest cent / render real decimals." My lean is **(a)**, but flag if you read the user's "0.0116" as wanting the literal figure → then **(b)**. I (orchestrator) decide at Step-2.5 review (lead delegated this one to me).
2. **Realtime stage-relabel rendering — single note vs per-row dash?** Options: **(a)** replace the three stage rows with ONE row "Single model — no discrete stages" [**my default vote** — actively explains *why* it's absent, which a bare `—` doesn't]; **(b)** keep three rows, each value `—`; **(c)** hide the three rows entirely. The user said "`n/a` → `—`/`single model`." My lean is **(a)**.
3. **Threshold for "sub-dime."** `< 0.10` (keeps cents for ≥$0.10) vs `< 0.01` (only strictly-sub-cent). My default vote: **`< 0.10`** — `0.0116` is ≥$0.01 yet still loses its signal at 2 decimals, so the threshold must be above a cent. Confirm this keeps `$0.42`/`$1.00` on the unchanged path (it does).

## Dependencies + sequencing
- **Depends on:** nothing — pure display over shipped surfaces.
- **Blocks:** nothing hard. Sibling to 075 (realtime $/min normalization — sends `audioDurationMs` at `/complete`); independent files, can land in any order. Both feed the G.5 write-up's cost axis.

## Estimated commit count
**1.** Two small related FE display fixes in the same metrics/cost-panel area, no safety invariant, bisectable as one "display polish" unit. The implementer goes RED → 2.5 → GREEN per feature, then ONE Step-10 commit. (Bundled per the "bundle 2-4 small related features" rule; distinct test files but one logical unit.)

## Lessons-logged candidates anticipated
- **Architecture-doc note candidate** — ARCH-013: realtime renders a single-model stage treatment (not `n/a`) because it has no discrete STT/Translation/TTS stages; ARCH-014: sub-cent cost/min renders ≥2 sig figs.
- **Convention candidate (maybe)** — "a by-design-absent metric reads as an explanatory token, never the missing-data `n/a`" (only if it generalizes beyond this panel).

## How to invoke
1. Read this brief end-to-end (don't skip Step-2.5 — the precision formula + relabel rendering need answers before GREEN).
2. Run `/tdd metrics_cost_display_polish`.
3. Step 0 (Restate) — confirm against the Feature line.
4. Step 2.5 — send the per-test write-up + your read on the three design questions; I'll reply with `APPROVED.`/`TWEAK:`/`ADD:` (I own the precision-formula call).
5. Step 9 — categorized summary + ship/no-ship + draft commit message.
