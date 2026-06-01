# Bidirectional Conversation — Phase Design + 3 Load-Bearing Sub-Decisions

> Authored by `boostlingo-main-orchestrator` for the lead → user (AskUserQuestion). Mapped against the real codebase + current Deepgram docs. **No brief dispatch until the 3 sub-decisions settle.**

## Read-back (cycle Step 4)
- `/orchestrate-start` run (not `/session-start`).
- Registry written: `boostlingo-main-orchestrator`, team `boostlingo-main`, role orchestrator, cwd = repo root.
- cwd = repo root (`ai-interpreter-workbench/`).
- Handoff doc 020 read + MVP_TASKS "Currently in progress" + Carry-forward. HEAD `9d0de83` (sealed); BE 439 / web 305 green. Both `boostlingo-main-` impls verified up.
- Seal residual (non-blocking): 071 `TurnDetailView` cross-doc row missing from `web/CLAUDE.md` → fold into round-close.

## The capability
Within ONE live session, per utterance: **auto-detect the spoken language → translate to the OTHER language → use VAD speaker-stop to know when to emit.** A real two-person back-and-forth (EN→ES one way, ES→EN the other), hands-free.

## Key finding — the architecture is already ~70% bidirectional-ready
Direction is **already per-turn** everywhere:
- `TurnViewModel.direction` (FE) + `CascadeStartParams.Direction` (BE) — every turn carries its own `{source,target}`.
- The cascade WS `start` frame already carries `direction:{source,target}` (`cascadeStreamClient.buildStartFrame` → `CascadeStartValidation`).
- `TranslationRequest` takes explicit `SourceLanguage`+`TargetLanguage`; the OpenAI translation instruction is built from them.
- TTS resolves voice by **target** language via `OpenAiTtsMapping.ResolveVoice` + `OpenAiTtsOptions.VoiceByLanguage` (keyed `"en"`/`"es"`).
- Realtime instructions are server-templated on `{source}/{target}` (`RealtimeClientSecretMapping.RenderInstructions`).

So bidirectional = **add auto-detect + flip direction per utterance**, NOT rebuild the pipeline.

## Reusable capability vs mode-specific
**Shared/reusable:** per-turn `direction` model + VAD speaker-stop (Phase-I auto-VAD, 062/073 — both modes already detect speaker-stop) + an additive "bidirectional" enable-flag + the transcript UX. This same capability later powers the **G.4 5-min synthetic soak-harness** (scripted EN↔ES audio injected programmatically) — out of scope for these slices, but it's why we build bidirectional as a first-class capability.

**Mode-specific — how each detects the source language:**
- **Realtime (lighter lift):** swap the instruction template from `"render {source}→{target}"` → bidirectional `"detect EN vs ES, render in the OTHER; speak only the translation."` `gpt-realtime` does detect+translate in one model; auto-VAD already gives speaker-stop; `gpt-4o-transcribe` transcribes either language.
- **Cascade (more wiring):** Deepgram nova-3 `multi` is ALREADY configured, but the detected language is **discarded** today — `SttFinal` = `{Text,Timestamp}` only; no `languages`/`detected_language` capture anywhere (grep-verified). Docs-verified: streaming `language=multi` reports source via `channel.alternatives[0].languages[]` + per-word `language` tags (e.g. `"languages":["es"]`, `"word":"será","language":"es"`). Capture it → flip direction → existing translation + target-TTS-voice flow finishes the job.

**One wrinkle (both modes):** a turn's stamped `direction` (display/metrics) is unknown until detection — cascade gets it free from Deepgram's signal; realtime infers it from the input-transcript language.

---

## (a) Cascade language-detect / dynamic-direction approach
- **Option A — Ride Deepgram's streaming detection (RECOMMENDED).** Surface `languages[]`/per-word `language` on `SttFinal`; orchestrator picks the dominant per-utterance language → flips direction. *Tradeoff:* a REAL provider signal we already pay for; deterministic + TDD-able; minimal new wiring. Mixed/code-switched single utterances are ambiguous (mitigate: pick dominant language, fall back to prior direction).
- **Option B — Separate language-ID on the transcript text** (heuristic or small LLM classify). *Tradeoff:* +1 call → added latency & cost; ignores a signal we already receive.
- **Option C — Translation LLM auto-detects** ("translate to whichever of EN/ES it isn't"), derive target-TTS language post-hoc. *Tradeoff:* fewest moving parts, but the source language for direction-stamping + TTS-voice becomes a guess, not a measured signal — weak for a measurement workbench.
- **Recommendation: A.** **Impact:** A = no added cost/latency (rides existing STT); B = adds a call; C = risks wrong TTS voice + unreliable direction labels (pollutes the comparison's direction attribution).

## (b) Full-cascade-bidirectional lift / phasing
- **Lift estimate:** realtime-bidir = SMALL (prompt-template change + mint flag). cascade-bidir = MODERATE (`SttFinal.DetectedLanguage` + Deepgram parse + orchestrator direction-flip; all TDD-able, no safety invariant). Neither is a blocker.
- **Option A — Phase by mode: realtime-bidir FIRST, cascade-bidir SECOND (RECOMMENDED).** Realtime validates the UX/transcript panel + the enable-flag with the lighter lift; cascade follows. BOTH ship in full — sequencing, NOT a cut (timebox OFF).
- **Option B — Bundle both modes in one push.** Bigger single review/Step-2.5 surface; loses incremental validation.
- **Option C — Realtime-bidir only, cascade-bidir DEFERRED.** A genuine scope cut — NOT recommended (timebox OFF; cascade lift is moderate).
- **Recommendation: A.** **Impact:** none of these change cost/latency/the comparison — purely sequencing/scope. Only C would cut comparison parity.

## (c) Both-directions transcript UX
- **Option A — Single chronological stream + per-turn direction badge (RECOMMENDED).** Each turn = a card with an "EN→ES"/"ES→EN" arrow, source+target inside. Keeps both original+translation visible, preserves chronology, minimal FE lift; right altitude for a workbench.
- **Option B — Two fixed language lanes (EN lane / ES lane),** each utterance+translation placed by detected source. Reads like a two-person log; medium FE lift.
- **Option C — Chat-bubble conversation (alternating L/R by speaker),** translation prominent, source small. Most demo-polished; biggest FE lift; risks hiding the side-by-side original/translation the workbench values.
- **Recommendation: A** — but most genuinely preference-driven (demo-judged), so user taste should drive it; C is the "demo polish" choice. **Impact:** pure UX/FE-lift; no cost/latency/data-model change (per-turn `direction` already exists for all three).

---

## ✅ FINALIZED (user decisions, via lead AskUserQuestion — 2026-05-31)
- **(a) Cascade lang-detect → Option A** — ride Deepgram's streaming detection; dominant-language pick + prior-direction fallback for ambiguous/mixed utterances. *(matched rec)*
- **(b) Phasing → Option B — BUNDLE BOTH MODES in one push** *(OVERRIDE of the realtime-first rec).* Realtime-bidir + cascade-bidir are built and **land in the same round** — cascade is NOT gated on realtime validation. Internal decomposition into clean TDD slices is fine; the round ships both.
- **(c) Transcript UX → Option A** — single chronological stream + per-turn direction badge. *(matched rec)*

This becomes **Phase J (Bidirectional)** — REVISES the ARCH-002/003 one-direction-EN→ES locked decision (additive; one-direction preserved), the same way Phase I revised ARCH-003 for auto-VAD.

## 🔌 Finalized wire contract (single source of truth — briefs cite this)

**Enable flag (both modes, additive — one-direction stays default):**
- Cascade WS `start` frame: optional `bidirectional?: boolean` (default `false`; mirrors the existing `autoVad` flag) → `CascadeStartParams.Bidirectional`.
- Realtime mint `POST /api/realtime/client-secret`: `RealtimeTokenRequest` gains `bidirectional` (default `false`). `true` → broker renders the bidirectional instruction template.

**Cascade per-utterance detection + direction resolution (BE):**
- `SttFinal` gains `DetectedLanguage` (`LanguageCode?`; null when undetected / one-direction). The Deepgram provider derives it from the streaming `languages[]`/per-word `language` signal → **dominant** language. *(Impl-internal: rides the SDK's typed fields if exposed, else a raw-JSON fallback — verified at Step-2.5 per §19.)*
- Orchestrator, when `Bidirectional`: on each `SttFinal`, resolve `direction = detected → other` (dominant; if detection null/ambiguous → fall back to the start-frame `Direction`). Use the resolved direction for the translation request + the TTS target-voice resolution (both already flow from direction).
- New cascade output event **`Direction(LanguageDirection)`** → new WS server message **`{ type: "direction", direction: { source, target } }`**, emitted once per utterance when the direction resolves (at `SttFinal`, before translation). NOT emitted in one-direction mode (backward compatible). Also **folded into `AssembleTurn`** so the persisted/assembled turn's `Direction` reflects the resolved per-utterance direction (FE + persistence + metrics stay consistent).

**FE consumption:**
- Cascade: on `{type:"direction"}`, stamp the live turn's `direction`. One-direction turns keep the start-frame direction (unchanged).
- Realtime: a **client-side deterministic EN/ES heuristic** on the source-input transcript stamps the turn's `direction` (display-only badge; falls back to the configured source on ambiguity). Documented limitation: best-effort (realtime emits no explicit language tag).

**Transcript UX (Option A):** single chronological stream; each turn = a card with a direction badge ("EN→ES"/"ES→EN" arrow) + source/target inside. Consumes `turn.direction`. No `ModeSummary` change (comparison stays by-mode; direction is not a summary axis).

**Realtime bidirectional instruction template:** *"You are a faithful realtime interpreter. The speaker may talk in English or Spanish. Detect which language they are speaking and render their words in the OTHER language. Speak only the translation — no commentary, no preamble."*

## Slice decomposition → briefs (all land in one round)
- **078 (BE / cascade):** `SttFinal.DetectedLanguage` + Deepgram language parse (dominant helper) → orchestrator per-utterance direction-flip gated on `Bidirectional` + the `Direction` output event + `AssembleTurn` fold. *(2 commits: detection-capture, then direction-flip — the `SttFinal` contract change stays bisectable.)*
- **079 (BE / realtime):** `RealtimeTokenRequest.Bidirectional` + the bidirectional instruction template in `RenderInstructions`/`BuildRequestBody`.
- **080 (FE / wiring):** the "Bidirectional / auto-detect" toggle in session config → sends `bidirectional:true` in the cascade start frame + the realtime mint; consumes the new `{type:"direction"}` cascade message → stamps `turn.direction`; the realtime client-side EN/ES heuristic stamps realtime `turn.direction`. TS contract mirrors.
- **081 (FE / transcript):** the chronological-stream + per-turn direction-badge transcript UX (Option A).

**Cross-doc (orchestrator writes hot during the round):** `SttFinal` row (ARCH-012/App A), `CascadeStartParams.Bidirectional` + the cascade WS `start`/`direction` messages (ARCH-009/011), `RealtimeTokenRequest.Bidirectional` + the bidir template (ARCH-009/010), and the ARCH-002/003 Phase-J revision note.
