# /tdd brief — soak_engine_deterministic_core

## Feature
The **deterministic core** of the G.4 5-minute synthetic soak-harness: the canonical scripted EN↔ES conversation model, the 1×-real-time **schedule computation**, the **drift / overlap / playback-skew / memory-leak measurement math**, and the structured **soak-report assembly**. All **pure + fixture-tested** — no browser audio, no network, no live providers. The runtime plumbing (audio injection, heap sampling, live drive, dev entry) is a **separate later slice (088)** that wires this core to the real app — mirroring the backend's §18/§20 "thin smoke shell over a pure TDD'd core" pattern.

## Use case + traceability
- **Task ID:** G.4-FE-core (the 5-min synthetic soak-harness — frontend deterministic half).
- **Architecture sections it implements:** `ARCHITECTURE.md §15 / ARCH-020` (the 5-min stability checks: no disconnect / no drift-overlap / no leak), `§10 / ARCH-013` (the `LatencyEvent` series the drift math reads), `§12 / ARCH-015` (WER aggregation), `§4 / ARCH-007` (clean-separation — the harness reads metrics/store, never bypasses it).
- **Related context:** `docs/g4-harness-design.md` — the full design + the **4 user-settled sub-decisions**. The two that pin THIS slice:
  - **Drift metric (decision 2A + 2C):** primary = per-turn end-to-end-latency **slope trend** + **overlap detection**; secondary = **playback-clock-vs-wall-clock skew**. Disconnect = transport-close count (input); leak = `performance.memory` heap-sample **trend** (the sampling is runtime/088; the **verdict from samples** is pure/here).
  - **Script cadence (decision 3A):** ~24 utterances (12 EN + 12 ES), ~8–12 words each, ~12 s/turn, realistic help-desk gaps, ~5 min; alternating source language (exercises bidirectional detect+flip both ways). Option-B dense variant is OUT of canonical scope (optional stress variant later — do NOT build it here).

## Acceptance criteria (what "done" means)
- [ ] **Script model** — a canonical, committed `SoakScript` (the ~24 travel/help-desk utterances, each `{ id, sourceLang: 'en'|'es', text }`) — the **known ground-truth** for WER-via-script. A pure validator pins shape invariants (alternates EN/ES, count ≈ 24, every utterance non-empty, total cadence ≈ 5 min given the configured gap).
- [ ] **Schedule computation** — given the ordered utterance **durations** (ms, supplied at runtime from decoded audio) + a configured inter-utterance `gapMs`, compute the **cumulative start offsets** + **expected playback-end times** at 1× real-time. Pure; deterministic.
- [ ] **Drift — latency slope** — given an **ordered per-turn end-to-end latency series** (ms), compute the linear-regression **slope (ms/turn)**; a `driftVerdict` flags PASS when |slope| ≤ a configured threshold (flat ⇒ no accumulating lag), FAIL when it trends up. Pure.
- [ ] **Drift — overlap detection** — given the schedule (expected start offsets) + per-turn **playback-complete** stamps, detect any **unplanned overlap** (turn N+1's scheduled injection begins before turn N's playback completes when the script says it shouldn't); report the offending turn pairs. Pure.
- [ ] **Playback-skew (secondary)** — given samples of `{ audioClockMs, wallClockMs }`, compute the skew **slope**; surfaced as a secondary signal in the report. Pure.
- [ ] **Leak verdict** — given an ordered series of heap samples (`usedJSHeapSize`), return a verdict: PASS when the trend **plateaus after warm-up**, FAIL on a sustained monotonic climb. Pure (the sampling is runtime/088; the math is here).
- [ ] **WER-via-script aggregation** — given per-turn `{ referenceText (from the script), werValue }`, aggregate per-mode (mean + median WER, sample count). Pure (the per-turn `/wer` call is runtime/088; the aggregation is here). Unbounded — never clamp WER > 1.0 (ARCH-015 / §10 / §12).
- [ ] **Soak-report assembly** — assemble a structured `SoakReport` `{ mode, durationSec, turnCount, disconnectCount, latencySlope+verdict, overlaps, skewSlope, heapLeakVerdict, werSummary, arch020: { noDisconnect, noDriftOverlap, noLeak } }` from the above inputs; the three ARCH-020 booleans derive from the verdicts + disconnectCount. Pure.
- [ ] All unit tests in `web/src/soak/*.test.ts` pass; `tsc --noEmit` strict clean (no `any` on exported surfaces); `npm run lint` + `npm run format:check` clean; `/preflight` clean.

## Wiring / entry point (Step 7.5)
This slice is **pure library code** — its production entry point is the **088 runtime harness** (next slice), which feeds it the live latency series / playback stamps / heap samples / per-turn WER and renders/loga the `SoakReport`. So 087 is **not yet reachable from a production UI path** — that's expected + explicitly deferred to 088 (note this at Step 7.5 rather than treating it as a reachability gap; 088's brief names the dev entry point). Pin reachability **within the slice** via the test suite exercising every exported function; flag at Step 7.5 that production wiring lands in 088.

## Files expected to touch
**New (under `web/src/soak/`):**
- `soakScript.ts` — the canonical `SoakScript` data + `validateSoakScript`.
- `soakSchedule.ts` — `computeSchedule(durationsMs[], gapMs)` → offsets + expected-end times.
- `soakDrift.ts` — `latencySlope(series)` + `driftVerdict`; `detectOverlaps(schedule, playbackEndStamps)`; `playbackSkewSlope(samples)`.
- `soakLeak.ts` — `leakVerdict(heapSamples, warmupCount, threshold)`.
- `soakWer.ts` — `aggregateWer(perTurn[])` → `{ meanWer, medianWer, count }`.
- `soakReport.ts` — `assembleSoakReport(inputs)` → `SoakReport`; types.
- matching `*.test.ts` for each.

**Modified:** none expected (pure new area). If you need to touch existing FE files, **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`script_alternates_and_covers_5min`** (`soakScript.test.ts`) — `validateSoakScript` passes the canonical script (alternates EN/ES, ~24 non-empty utterances) + rejects a malformed one. Why: decision 3A; the known-text source for WER.
2. **`schedule_offsets_cumulative_at_1x`** (`soakSchedule.test.ts`) — durations `[1000, 1500]` + `gapMs 500` → offsets `[0, 1500]` (1000+gap) + expected ends. Why: 1×-real-time pacing constraint.
3. **`latency_slope_flat_passes_rising_fails`** (`soakDrift.test.ts`) — a flat series → slope ≈ 0, PASS; a rising series → positive slope, FAIL at threshold. Why: decision 2A drift = accumulating-lag trend.
4. **`detect_overlap_flags_collision`** (`soakDrift.test.ts`) — a schedule where turn N+1 starts before turn N's playback-end → flagged with the pair; a clean schedule → none. Why: decision 2A overlap.
5. **`playback_skew_slope`** (`soakDrift.test.ts`) — synthetic `{audioClock, wallClock}` samples → expected skew slope. Why: decision 2C secondary.
6. **`leak_verdict_plateau_vs_climb`** (`soakLeak.test.ts`) — a plateauing heap series → PASS; a sustained climb past warm-up → FAIL. Why: ARCH-020 no-leak; `performance.memory` trend.
7. **`aggregate_wer_mean_median_unbounded`** (`soakWer.test.ts`) — per-turn WER `[0, 0.5, 1.5]` → mean/median correct + **not clamped** at 1.0. Why: ARCH-015 unbounded (§10/§12 clamp-sensitive fixture).
8. **`assemble_report_derives_arch020_booleans`** (`soakReport.test.ts`) — given verdicts + `disconnectCount 0` → `arch020.noDisconnect/noDriftOverlap/noLeak` all true; flip one input → the matching boolean flips. Why: ARCH-020 three-check gate.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none (new pure types under `web/src/soak/`; the harness **consumes** existing contracts — `LatencyEvent`, WER, the cascade/realtime stamps — and adds no persisted field).
- **Orchestrator doc rows to write hot (Step 9 routing):** likely none (no contract surface). A `web/CLAUDE.md` lookup-table row for the new `web/src/soak/` area may be worth adding (orchestrator decides at routing). Any lesson candidate (e.g. the pure-core / smoke-shell split for a FE harness) → flag at Step 9.

## Things to flag at Step 2.5
1. **Drift slope threshold + units.** Default: slope in **ms/turn** over the ~24-turn series, PASS when |slope| ≤ a small configured constant (start ~50 ms/turn; tune at the live run). Vote: **ms/turn, configurable constant** — the live 088 run calibrates it; the test pins the math, not the magic number.
2. **Leak verdict heuristic.** Default: discard the first `warmupCount` samples, then FAIL if the linear trend over the remainder exceeds a configured climb threshold (and PASS if it plateaus). Vote: **warm-up-then-trend** — robust to startup allocation; the threshold is configurable. (`performance.memory` is Chrome-only — the documented demo path; acceptable per ARCH-020/the carry-forward Chrome-demo note.)
3. **WER aggregation — mean vs median headline.** Default: report **both** mean + median + count; the comparison write-up (G.5) picks. Vote: **both** — cheap, and median resists a single-outlier utterance.
4. **Script storage form.** Default: the `SoakScript` is **committed TS data** in `soakScript.ts` (the script text is the committed source of truth per decision 4; only the synthesized WAVs are gitignored). Vote: **committed TS data** — matches decision 4.

## Dependencies + sequencing
- **Depends on:** nothing — pure, fixture-based; independent of the BE endpoint (086) and browser audio.
- **Blocks:** **088** (the runtime harness: audio injection via the `getUserMedia` DI seam + cached audio from 086, heap/disconnect sampling, live drive, dev entry) consumes every export here. **089** runs it 5 min × both modes + records into the ARCH-020 checklist + G.5.

## Estimated commit count
**1** (preferred) — one cohesive soak-engine core; each module gets its own RED-test section, one Step-10 commit. No safety invariant. **If at Step 2.5 you judge the bundle too large**, the natural split is **(a) measurement math** (`soakDrift`/`soakLeak`) and **(b) script + schedule + WER-aggregation + report** — flag it and split into two commits; don't atomize further (over-atomizing pitfall).

## Lessons-logged candidates anticipated
- **Convention candidate** — "a FE soak/measurement harness is a **thin runtime smoke-shell over a pure TDD'd core** (script/schedule/drift/leak/report computed deterministically on fixtures; only the audio-injection + heap-sampling glue is smoke-exempt) — the FE analogue of the §18/§20 backend real-provider pattern."
- **Architecture-doc note candidate** — ARCH-020's three 5-min checks are now computed by an explicit, testable engine (drift = latency-slope + overlap; leak = warm-up-then-trend on `performance.memory`), not just an eyeball pass.

## How to invoke
1. Read this brief end-to-end + skim `docs/g4-harness-design.md` (decisions 2A/2C + 3A pin this slice).
2. Run `/tdd soak_engine_deterministic_core` in the frontend implementer session.
3. Step 0 (Restate) → confirm against the Feature line.
4. Step 2.5 → send the test write-up + answers to the 4 questions (or take defaults).
5. Step 7.5 → note that production wiring lands in 088 (this slice is a pure library; not a reachability gap).
6. Step 9 → surface any new-area `web/CLAUDE.md` lookup row + lesson candidate.
