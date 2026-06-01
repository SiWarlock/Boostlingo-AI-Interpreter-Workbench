# /tdd brief — frontend_bidirectional_wiring

## Feature
Add a "Bidirectional / auto-detect" toggle to session config; when on, send `bidirectional:true` in the cascade WS `start` frame AND the realtime mint request. Consume the new `{type:"direction"}` cascade WS message to stamp the live turn's `direction`. For realtime, stamp the turn's `direction` via a client-side deterministic EN/ES heuristic on the source-input transcript (display badge; best-effort, realtime emits no explicit language tag).

## Use case + traceability
- **Task ID:** J.3 (Phase J — Bidirectional)
- **Architecture sections it implements:** ARCH-007 (SPA/transcripts), ARCH-009 (WS + mint contracts), ARCH-010 (realtime)
- **Related context:** `docs/bidirectional-phase-design.md` (🔌 Finalized wire contract). Producer side: cascade-078 (the `{type:"direction"}` message + the `bidirectional` start flag) and realtime-079 (`RealtimeTokenRequest.bidirectional`). Transcript rendering is the sibling brief 081 — THIS brief is the data/wiring; 081 is presentation.

## Acceptance criteria (what "done" means)
- [ ] A "Bidirectional / auto-detect" toggle exists in session config; store state `bidirectional: boolean` (default `false`); togglable pre-session like the language-pair selector.
- [ ] When enabled, `buildStartFrame` includes `bidirectional: true` (omitted/false when off — back-compat with cascade-078's default).
- [ ] When enabled, the realtime mint request body includes `bidirectional: true`.
- [ ] On a cascade `{type:"direction", direction:{source,target}}` message, the current turn's `direction` is updated in the store.
- [ ] Realtime: the turn's `direction` is stamped from a deterministic `detectLanguage(sourceText) → 'en'|'es'` heuristic (→ `{source: detected, target: other}`), falling back to the configured source on ambiguity.
- [ ] TS mirrors updated: the cascade start-frame `bidirectional` field, the `RealtimeTokenRequest` `bidirectional` mirror, and the `{type:"direction"}` cascade message type.
- [ ] All Vitest units pass; `tsc --noEmit` clean; `/preflight` clean.

## Files expected to touch
**New:**
- `web/src/util/detectLanguage.ts` — deterministic EN/ES heuristic + its test.

**Modified:**
- `web/src/state/sessionStore.ts` — `bidirectional` state + toggle action; consume the `direction` message → set current turn `direction`.
- `web/src/cascade/cascadeStreamClient.ts` — `buildStartFrame` includes the flag; handle the inbound `{type:"direction"}` message.
- `web/src/realtime/realtimeTurnController.ts` (or the mint-call site) — pass `bidirectional` to the mint; stamp realtime turn direction via the heuristic on source transcript.
- `web/src/types/domain.ts` (+ any cascade/realtime message-type modules) — the new contract fields/message type.
- The session-config UI component (find at Step 1 — the language-pair selector's neighbor) — the toggle.

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`detectLanguage_spanish_text_returns_es`** — Spanish markers (`¿`/`¡`/diacritics/stopwords like "hola, gracias, está") → `'es'`. Why: heuristic correctness.
2. **`detectLanguage_english_text_returns_en`** — plain English → `'en'`. Why: heuristic correctness.
3. **`detectLanguage_ambiguous_falls_back`** — empty/numeric/ambiguous → the documented fallback. Why: no crash, deterministic default.
4. **`buildStartFrame_includes_bidirectional_when_enabled`** — flag present when on, absent when off. Why: back-compat with cascade-078.
5. **`cascade_direction_message_updates_turn_direction`** — a `{type:"direction"}` message sets the current turn's `direction`. Why: wire contract.
6. **`realtime_mint_request_includes_bidirectional`** — the mint body carries the flag when enabled. Why: realtime-079 contract.
7. **`realtime_turn_direction_from_source_heuristic`** — a Spanish source transcript ⇒ turn direction `es→en`. Why: realtime direction attribution.

## Cross-doc invariant impact
- **Model field changes:** TS mirrors only (the backend records are 078/079's). The cascade start-frame `bidirectional` + the `{type:"direction"}` message + `RealtimeTokenRequest.bidirectional` are wire-contract mirrors — flag at Step 9 so the orchestrator notes the ARCH-007/009 TS-mirror rows.

## Things to flag at Step 2.5
1. **Heuristic approach** — diacritics + `¿`/`¡` + a small Spanish stopword set vs a char-ngram model? **Default vote: diacritics + markers + stopword set, fallback to configured source.** Cheap, deterministic, sufficient for a display badge (NOT a measured signal — documented).
2. **Does enabling bidirectional auto-enable auto-VAD?** Hands-free back-and-forth really wants VAD on. **Default vote: keep them INDEPENDENT flags, but have the UI default both on together for the bidirectional path** (a sensible-default, not a hard coupling). Confirm the UX with the user's taste if unsure.
3. **Mid-session toggle?** **Default vote: pre-session only** for this slice (consistent with the existing config-gating); a mid-session bidirectional toggle is a follow-up if wanted.

## Dependencies + sequencing
- **Depends on:** the wire contract from cascade-078 + realtime-079 (defined; lands same round). FE can build against the agreed contract in parallel.
- **Blocks:** 081 (transcript UX) consumes the now-correct per-turn `direction`.

## Estimated commit count
**1–2.** The `detectLanguage` heuristic may land as its own small commit; the wiring as a second. Same area, related context — bundle if tidy.

## Lessons-logged candidates anticipated
- **Convention candidate** — realtime direction attribution is a best-effort client heuristic (no provider language tag); cascade rides the backend-resolved `direction` message (a measured signal). Document the asymmetry.
