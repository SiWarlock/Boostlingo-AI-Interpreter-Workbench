# /tdd brief — realtime_bidirectional_instruction

## Feature
When the Realtime mint request sets `bidirectional:true`, the broker renders a **bidirectional** interpreter instruction ("the speaker may talk in English or Spanish — detect which and render in the OTHER language") instead of the one-direction `{source}→{target}` template. `RealtimeTokenRequest` gains the `bidirectional` flag (additive; default `false` keeps today's one-direction behavior).

## Use case + traceability
- **Task ID:** J.2 (Phase J — Bidirectional; REVISES ARCH-002/003 one-direction lock, additive)
- **Architecture sections it implements:** ARCH-010 (Realtime broker / interpreter instructions), ARCH-009 (`POST /api/realtime/client-secret` request DTO)
- **Related context:** `docs/bidirectional-phase-design.md` (the 🔌 Finalized wire contract — single source of truth). 073 fixed the realtime `session.type` so config now applies (so the instruction is actually honored). Cascade's half is brief 078; FE wiring is 080.

## Acceptance criteria (what "done" means)
- [ ] `RealtimeTokenRequest` carries `bool Bidirectional` (trailing-defaulted `= false`; absent in JSON → `false`, back-compat).
- [ ] When `Bidirectional` is true, the rendered `session.instructions` instructs detect-EN-or-ES → render-the-OTHER, speak-only-the-translation; contains NO leftover `{source}`/`{target}` placeholders and does NOT hardcode a single direction.
- [ ] When `Bidirectional` is false, the instruction is **byte-identical** to today's one-direction `{source}→{target}` render (regression pin).
- [ ] The GA `client_secrets` request body's `session.instructions` reflects the chosen template (threaded end-to-end through `BuildRequestBody`).
- [ ] `direction` is still sent on the request when bidirectional (it's the realtime turn's initial/fallback direction; the FE heuristic refines per-turn) — not dropped.
- [ ] `/preflight` clean. Cross-doc invariant (`RealtimeTokenRequest` + bidir template) flagged at Step 9 (orchestrator writes the rows).

## Files expected to touch
**Modified:**
- `server/AiInterpreter.Api/Realtime/RealtimeModels.cs` — `RealtimeTokenRequest` + `bool Bidirectional = false`.
- `server/AiInterpreter.Api/Realtime/RealtimeClientSecretMapping.cs` — a `DefaultBidirectionalInstructionsTemplate` const; `RenderInstructions`/`BuildRequestBody` select it when bidirectional.
- `server/AiInterpreter.Api/Realtime/RealtimeClientSecretService.cs` — thread `request.Bidirectional` into `BuildRequestBody` (confirm the current call site at Step 1).
- The controller that constructs `RealtimeTokenRequest` from the HTTP body (RealtimeController — confirm at Step 1) — accept the new field.
- `server/AiInterpreter.Tests/...` — the realtime mapping test file (extend).

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`RenderInstructions_bidirectional_detects_both_renders_other`** — Asserts: with `bidirectional:true` the string contains detect/both-language + "other"/"opposite" intent, NO `{source}`/`{target}` substrings, and is not the one-direction template. Why: ARCH-010 bidir instruction.
2. **`RenderInstructions_one_direction_byte_identical_regression`** — Asserts: `bidirectional:false` ⇒ exactly today's `{source}→{target}` render for En→Es. Why: additive, no regression.
3. **`BuildRequestBody_threads_bidirectional_into_instructions`** — Asserts: the built body's `session.instructions` equals the bidir render when the flag is set. Why: end-to-end thread.
4. **`RealtimeTokenRequest_absent_bidirectional_defaults_false`** — Asserts: deserializing a body without `bidirectional` yields `false`. Why: back-compat / trailing-default.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** `RealtimeTokenRequest` gains `Bidirectional` (the TS mirror gains it too — that mirror is FE-080's; flag the BE side here).
- **Orchestrator doc rows to write hot:** `RealtimeOptions`/`RealtimeTokenRequest` cross-doc row note in `server/CLAUDE.md` + ARCH-009/010 Appendix-A mirror; the ARCH-002/003 Phase-J revision note (orchestrator writes once for the round).

## Things to flag at Step 2.5
1. **`RenderInstructions` signature** — add a `bool bidirectional` param to the existing method vs a separate `RenderBidirectionalInstructions`? **Default vote: add the param** — one entry point, the call sites already pass `direction`.
2. **Custom bidir template config** — a new `RealtimeOptions.BidirectionalInstructionsTemplate` (mirroring the one-direction `InstructionsTemplate` override) vs a const-only default? **Default vote: const-only default now** (YAGNI; add the option only if `RealtimeOptions` shows the one-direction override is actually used — check at Step 1). If you add the option, it's another cross-doc row.
3. **Keep `direction` in the bidir request?** **Default vote: yes** — it's the realtime turn's initial/fallback direction (the FE-080 heuristic refines per turn); dropping it would lose the fallback.

## Dependencies + sequencing
- **Depends on:** nothing (independent of cascade-078). Lands in the Phase-J round.
- **Blocks:** FE-080 consumes `RealtimeTokenRequest.bidirectional` (the TS mirror) — contract defined here.

## Estimated commit count
**1.** Focused realtime-instruction change; small, single subsystem, no safety invariant.

## Lessons-logged candidates anticipated
- **Architecture-doc note candidate** — ARCH-010: the bidirectional instruction mode (detect-and-render-other) as an additive realtime capability.
