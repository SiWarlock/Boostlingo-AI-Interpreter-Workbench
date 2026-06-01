# /tdd brief — soak_overlap_and_disconnect

## Feature
Complete the soak harness's drift-decision 2A: feed a per-turn **output-audio duration** into the soak's `resolveOutputDurationMs` so `playbackEndMs` becomes a real value → 087 `detectOverlaps` runs → `overlapMeasured` flips true (real overlap detection). Plus replace 089b's failed-turn disconnect *proxy* with the **precise** WS-close / pc-`failed` hook. This is the last code slice before the manual run.

## Use case + traceability
- **Task ID:** G.4-FE-overlap (Consumer A of the output-audio work — the genuine remaining piece).
- **Architecture sections:** `ARCHITECTURE.md §15 / ARCH-020` (no disconnect / drift-overlap), `§10 / ARCH-013`, `§4 / ARCH-007`. Decision **2A** (latency-slope + overlap) — this completes the overlap half.
- **Related context:** consumes 087 (`detectOverlaps`, `overlapMeasured`), 089b (`soakStoreView.resolveOutputDurationMs`, currently → null), 092 (`turn.outputAudioTokens` on `TurnViewModel`). `docs/g4-harness-design.md` → output-duration plan.

## Acceptance criteria
- [ ] **Realtime overlap-duration** = `turn.outputAudioTokens ÷ tokens-per-second` (mirror the BE's `RealtimeTokensPerAudioSecond`; cite it) → ms. **Derived from the REPORTED tokens (disclosed derivation, not a fabricated metric).** Null when `outputAudioTokens` absent.
- [ ] **Cascade overlap-duration** — verify the available signal at Step-2.5 (the §36 char→minutes TTS estimate, the played-audio duration, or none). If a clean signal exists, derive it; **if none, honest-null** → cascade overlap stays disclosed-unmeasured (`overlapMeasured=false` for cascade) — don't fabricate.
- [ ] `soakStoreView.resolveOutputDurationMs(turn)` returns the per-mode duration (realtime token-derived; cascade per the above) → `playbackEndMs = playback.started(run-relative) + duration` → 087 `detectOverlaps` runs → `overlapMeasured` true for the mode(s) with a duration signal.
- [ ] **Precise disconnect hook** — replace the 089b failed-turn proxy: cascade WS `close` (the `cascadeStreamClient` close/terminal) + realtime pc `connectionState` `disconnected`/`failed` (the `realtimeWebRtcClient` callbacks) → `SoakDrive.disconnectCount()`. So the `noDisconnect` ARCH-020 boolean reflects real transport disconnects.
- [ ] Deterministic derivations unit-TDD'd (token→ms; resolver per mode; overlap-now-runs); the live transport-close capture is smoke. `tsc`/lint/format/`/preflight` clean.

## Wiring / entry point (Step 7.5)
`composeSoakDrive`/`runSoakHarness` (089b) → the resolver now returns real durations → the runner's overlap detection runs → the `SoakReport`'s `overlapMeasured`/`overlaps`/`noDriftOverlap` reflect reality. The disconnect hook feeds `disconnectCount()`. Reachable via the `?soak=1` dev entry; the full real-audio drive = the manual run.

## Files expected to touch
**Modified:** `web/src/soak/soakStoreView.ts` (the `resolveOutputDurationMs` derivation) + `composeSoakDrive.ts` (the precise disconnect hook) + a small `RealtimeTokensPerAudioSecond` mirror (constant). Tests alongside.

## RED test outline (Step 2)
1. **`realtime_duration_from_output_tokens`** — `turn.outputAudioTokens = 1500`, tokens/sec = 50 → duration 30000 ms (cite the constant); absent tokens → null. Why: the derived (reported-token) overlap-duration.
2. **`cascade_duration_per_step25_decision`** — the cascade signal (or honest-null). Why: per-mode overlap.
3. **`resolver_feeds_playback_end_and_overlap_runs`** — a turn with `playback.started` + a derived duration → `playbackEndMs` = start+duration → `detectOverlaps` produces results / `overlapMeasured` true (vs all-null → false). Why: closes decision-2A.
4. **`disconnect_count_from_transport_events`** — a faked WS `close` / pc `failed` → `disconnectCount()` increments; clean run → 0. Why: precise `noDisconnect`.

## Cross-doc invariant impact
- **Model field changes:** none new (consumes `turn.outputAudioTokens` from 092). The `RealtimeTokensPerAudioSecond` mirror is an internal constant. No cross-doc row expected.

## Things to flag at Step 2.5
1. **Cascade output-duration signal.** (a) the §36 char→minutes TTS estimate (disclosed, already used for cascade cost); (b) the played-audio duration (if reachable); (c) none → honest-null (cascade overlap stays disclosed-unmeasured). My lean: **(a) if the char-estimate is cleanly reachable per-turn** (consistent with the cascade cost basis + disclosed), else **(c) honest-null**. Verify what's reachable; don't fabricate.
2. **`RealtimeTokensPerAudioSecond` value/source.** Mirror the BE constant (cite it); hardcode with a comment vs config. Lean: hardcode-with-cite (a known conversion); flag if it should be config-sourced.
3. **Disconnect hook plumbing.** Confirm the `cascadeStreamClient` close + `realtimeWebRtcClient` `connectionState` callbacks are reachable from `composeSoakDrive`'s own client instances (they should be — it owns them). Lean: subscribe on the soak's own clients.
4. **Per-mode `overlapMeasured`.** If cascade is honest-null but realtime works, the per-mode `SoakReport` differs (realtime `overlapMeasured:true`, cascade `false`). Confirm that's the intended per-mode honesty (it is — each mode reports what it measured).

## Dependencies + sequencing
- **Depends on:** 092 (`turn.outputAudioTokens`) — landing now. 087/089b — landed.
- **Blocks:** the **manual real-key 5-min run × both modes** (the harness's first real exercise — captures overlap + the live cost wire-shape [CF76] + latency/leak/WER). This is the LAST code slice before that run.

## Estimated commit count
**1.** Focused overlap-completion + disconnect-hook; no safety invariant; FE-only.

## Lessons-logged candidates anticipated
- **Architecture-doc note** — decision-2A overlap is now real (realtime token-derived duration; cascade per Step-2.5); the soak's `noDisconnect`/`overlapMeasured` reflect real transport events + per-mode measurability. Folds into the ARCH-020 realization note.

## How to invoke
1. Read this brief + `docs/g4-harness-design.md`; you own 087/089b/092.
2. `/tdd soak_overlap_and_disconnect` → Step 0 → Step 2.5 (esp. Q1 the cascade signal) → Step 9. After this lands, the harness is run-ready.
