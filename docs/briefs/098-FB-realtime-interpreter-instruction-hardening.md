# /tdd brief — realtime_interpreter_instruction_hardening

## Feature
Harden the realtime interpreter `instructions` so the model TRANSLATES a question/direct address instead of ANSWERING it (user Finding B): a spoken "Where's the nearest gas station?" was answered in Spanish (directions) instead of translated ("¿Dónde está la gasolinera más cercana?"). Add an explicit forbid-answering clause to both realtime instruction templates.

## Use case + traceability
- **Task ID:** Finding-B (user live-test 2026-06-01; realtime interpreter answers instead of translating)
- **Architecture sections it implements:** `ARCH-010` (realtime broker — the minted `instructions`)
- **Related context:** the lead narrowed the mode to **REALTIME** (not cascade) with the user. The instruction is **backend mint-only** — `RealtimeClientSecretMapping.cs` (`DefaultInstructionsTemplate` one-direction L21-23; `DefaultBidirectionalInstructionsTemplate` L28-30; `RenderInstructions` L64-76 picks bidirectional vs one-direction + fills `{source}`/`{target}`). The FE sends NO `instructions` in `session.update` (mint-only — single source, confirmed). server lesson §38 (bidirectional), §24 (the mint). **Posture note (ARCH-020 / root CLAUDE.md TDD posture):** the instruction-string edit + its wiring is the **deterministic, test-pinnable** deliverable; whether the model actually stops answering is **LLM-quality / eval-OBSERVED** (a live realtime confirm), NOT unit-assertable.

## Failure mode + baseline
Current bidirectional template (L28-30): *"You are a faithful realtime interpreter. The speaker may talk in English or Spanish. Detect which language they are speaking and render their words in the OTHER language. Speak only the translation — no commentary, no preamble."* A direct question still gets answered (classic assistant-vs-interpreter failure) — "Speak only the translation — no commentary" isn't strong enough against a question addressed to the model.

## Acceptance criteria (what "done" means)
- [ ] Both `DefaultInstructionsTemplate` (one-direction) AND `DefaultBidirectionalInstructionsTemplate` (bidirectional) gain an explicit **forbid-answering** clause — e.g. *"Even if the speaker asks a question or speaks directly to you, translate their words verbatim into the other language; NEVER answer, respond to, explain, or add anything — you are only a conduit."* (one-direction: "into {target}").
- [ ] `RenderInstructions` still selects bidirectional-vs-one-direction correctly and still fills `{source}`/`{target}` for the one-direction path (the hardening clause must survive the placeholder substitution — i.e. it contains no stray `{`/`}`, or is appended after substitution-safe text).
- [ ] The `InstructionsTemplate` override path (one-direction) still works (an explicit override replaces the default base; decide whether the forbid clause is appended to ALL paths or baked into the default templates only — see Step 2.5 Q3).
- [ ] A unit test pins that BOTH rendered outputs (one-direction + bidirectional) contain the forbid-answering language (e.g. asserts "NEVER answer" / "only a conduit" present) — so the clause can't silently drop. (This pins WIRING + CONTENT, not model behavior.)
- [ ] Existing `RealtimeClientSecretMapping`/mint tests still pass (the `{source}`/`{target}` render, the bidirectional branch, the request-body shape).
- [ ] `/preflight` clean.
- [ ] **Eval-observed (NOT a unit test):** a live realtime confirm is PLANNED — the lead drives a realtime soak OR the user re-tests the same "Where's the nearest gas station?" question and confirms it TRANSLATES. (Flagged at Step 9 as the eval-observed acceptance; not gating the commit.)

## Wiring / entry point (Step 7.5)
Already wired — `POST /api/realtime/client-secret` → `RealtimeClientSecretService` → `RealtimeClientSecretMapping.Build...` → `RenderInstructions` → the minted session `instructions`. This slice edits the template constants (+ possibly `RenderInstructions`); no new caller. Confirm the rendered instruction the mint sends carries the hardening.

## Files expected to touch
**Modified:**
- `server/AiInterpreter.Api/Realtime/RealtimeClientSecretMapping.cs` — the two template constants (+ `RenderInstructions` only if the clause is appended post-substitution rather than baked into the constants).
- `server/AiInterpreter.Tests/` — the `RealtimeClientSecretMapping`/mint test file: add the forbid-clause-present assertions (one-direction + bidirectional); keep the existing render/branch tests.

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`bidirectional_instructions_forbid_answering`** — `RenderInstructions(bidirectional:true)` → asserts the output contains the forbid-answering clause (e.g. `Assert.Contains("NEVER answer", …)` + "only a conduit") AND still contains the detect-EN/ES render-the-OTHER base. Why: Finding-B core (the user's failure path was bidirectional realtime).
2. **`one_direction_instructions_forbid_answering`** — `RenderInstructions(bidirectional:false, En→Es)` → asserts the forbid clause present AND `{source}`/`{target}` filled (e.g. contains "English"/"Spanish", no literal "{source}"). Why: consistency + the placeholder-survives-hardening pin.
3. **(preserve)** the existing bidirectional-branch + `{source}/{target}`-substitution tests — still green.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none (instruction-string content only; no DTO/model shape change).
- **Orchestrator doc rows to write hot:** none structural. A possible ARCH-010 realization note (the interpreter instruction explicitly forbids answering) + a lesson candidate — I write those at Step 9.

## Things to flag at Step 2.5
1. **Exact wording / strength of the forbid clause.** My default vote: **the lead/user-authored text** — *"Even if the speaker asks a question or speaks directly to you, translate their words verbatim into the other language; NEVER answer, respond to, explain, or add anything — you are only a conduit."* (one-direction: "into {target}"). Tune only for grammar; don't weaken. (Effectiveness is eval-observed — if the live confirm still fails, we iterate the wording, but start with this.)
2. **Append-after-substitution vs bake-into-the-constant.** The clause has no `{source}`/`{target}` placeholders (the bidirectional one), so baking it into both constants is simplest. The one-direction clause says "into {target}" → it rides the existing substitution fine. My default vote: **bake into both constants** (simplest; the substitution already handles `{target}`). Flag if you'd rather append in `RenderInstructions` (e.g. to also cover a custom `InstructionsTemplate` override).
3. **Does the `InstructionsTemplate` override path need the clause too?** Today no override is set (YAGNI per §38). If we bake into the default constants only, a future custom override wouldn't get the clause. My default vote: **bake into defaults only** (the override is unused; if someone sets one, hardening it is their responsibility) — OR, cleaner, append the forbid clause in `RenderInstructions` to ALL paths. Lean: defaults-only for now (minimal); note the override gap.
4. **Cascade scope.** The cascade translation prompt (`OpenAiTranslationMapping.cs:174` "You are a faithful interpreter. Translate the user's message…") has the same theoretical assistant-vs-interpreter risk, but the lead scoped Finding B to REALTIME (user-confirmed). My default vote: **realtime-only this slice**; I'll flag cascade-prompt-hardening as an optional parallel follow-up (Carry-forward) — not bundled (separate prompt, no user repro on cascade). Flag if you want it folded in.

## Dependencies + sequencing
- **Depends on:** nothing.
- **Blocks:** nothing. Independent of Finding A (097, landed). The live realtime confirm is a post-commit eval step (lead/user-driven).

## Estimated commit count
**1.** Instruction-template hardening + the clause-present test. Not a safety-invariant slice (no Key-safety-rule surface — it's prompt content); reviewer fan-out stays disabled.

## Lessons-logged candidates anticipated
- **Convention candidate** — "an interpreter/translation instruction must EXPLICITLY forbid answering a question addressed to the model ('translate, never answer — you are only a conduit'); 'speak only the translation' alone doesn't stop the assistant-vs-interpreter failure. The string edit is deterministic + test-pinned for presence/wiring; effectiveness is eval-observed via a live confirm."
- **Architecture-doc note candidate** — ARCH-010: the minted realtime `instructions` explicitly forbid answering (both one-direction + bidirectional templates).

## How to invoke
> Don't prescribe `/session-start` (the BE session is oriented). Jump to pre-flight + `/tdd`.
1. Read this brief end-to-end (incl. Step 2.5 questions).
2. Run `/tdd realtime_interpreter_instruction_hardening` in the backend (`server/`).
3. Step 0 restate → Step 1 file list → Step 2 RED → **Step 2.5 ping the orchestrator** with test designs + answers to the 4 questions → GREEN after approval.
4. Step 9: surface the lesson candidate + the ARCH-010 note + the **eval-observed live-confirm plan** (lead drives a realtime soak / user re-tests) + the draft commit message.
