# /tdd brief — cascade_bidirectional_detection

## Feature
Make the cascade detect the spoken source language per utterance (Deepgram nova-3 `multi`, already configured) and dynamically flip the translation direction: surface `DetectedLanguage` on `SttFinal`, and — when the per-turn `bidirectional` flag is set — resolve `direction = detected → other`, drive translation + the target-language TTS voice off it, emit a `Direction` output event to the FE, and fold the resolved direction into the persisted turn. Additive: `bidirectional:false` (default) is byte-identical to today's one-direction behavior.

## Use case + traceability
- **Task ID:** J.1 (Phase J — Bidirectional; REVISES ARCH-002/003 one-direction lock, additive — preserves one-direction)
- **Architecture sections it implements:** ARCH-011 (cascade streaming orchestrator), ARCH-012 (`SttFinal` provider event), ARCH-009 (cascade WS `start` + the new `direction` server message), ARCH-019 (start-frame boundary validation)
- **Related context:** `docs/bidirectional-phase-design.md` (🔌 Finalized wire contract — single source of truth). SDK verified (reflection, lesson §19): `Deepgram.Models.Listen.v2.WebSocket.Alternative.Languages` (`IReadOnlyList<string>`, JSON `"languages"`) + `Word.Language` (`string`, JSON `"language"`) are exposed on the live WS path. **No raw-JSON fallback needed — ride the typed signal.** Caveat: those fields populate ONLY under `language=multi` (cascade already requests it via `SttLanguage="multi"`) — null-tolerate.

## Acceptance criteria (what "done" means)
- [ ] `SttFinal` carries `LanguageCode? DetectedLanguage` (trailing-defaulted `= null`; existing 2-arg construction unchanged — back-compat).
- [ ] The Deepgram mapping populates `DetectedLanguage` from the dominant language of `Alternative.Languages` (fallback: the mode of `Words[].Language`); `en*`→`En`, `es*`→`Es`, anything else → `null` (null-tolerant).
- [ ] `CascadeStartParams` carries `bool Bidirectional` (trailing-defaulted `= false`, after `AutoVad`); `CascadeStartValidation` accepts a `bidirectional` boolean on the `start` frame (absent → false).
- [ ] When `Bidirectional` and a segment's `DetectedLanguage` resolves: the orchestrator translates `detected → other` and the TTS voice follows the **resolved target language** (via `VoiceByLanguage`, not a single frame voice). When detection is null/ambiguous → fall back to the start-frame `Direction`.
- [ ] A new `Direction(LanguageDirection)` cascade output event is emitted per resolved segment (before translation) → WS message `{ type:"direction", direction:{source,target} }`. NOT emitted when `bidirectional:false`.
- [ ] `AssembleTurn` folds the resolved direction → the persisted/assembled turn's `Direction` (first resolved segment wins for the turn-level field).
- [ ] `bidirectional:false` path is byte-identical to today (regression-pinned): no `Direction` event, uses `p.Direction`.
- [ ] `/preflight` clean. Cross-doc invariants flagged at Step 9 (orchestrator writes rows).

## Files expected to touch
**Modified:**
- `server/AiInterpreter.Api/Providers/Abstractions/ProviderEvents.cs` — `SttFinal` + `LanguageCode? DetectedLanguage = null`.
- `server/AiInterpreter.Api/Providers/Deepgram/DeepgramSttMapping.cs` — read `.Languages`/`.Words[].Language` alongside `.Transcript`; a pure dominant-language helper (`en*/es*`→`LanguageCode?`).
- `server/AiInterpreter.Api/Cascade/CascadeModels.cs` — `CascadeStartParams.Bidirectional`; new `Direction(LanguageDirection)` output event.
- `server/AiInterpreter.Api/Cascade/CascadeStartValidation.cs` — parse `bidirectional` (boolean, default false).
- `server/AiInterpreter.Api/Cascade/CascadeStreamingOrchestrator.cs` — per-segment resolved direction (translation ~line 277-278, TTS ~line 368); emit `Direction`.
- `server/AiInterpreter.Api/Cascade/CascadeWsMapping.cs` — map `Direction`→`{type:"direction"}`; fold into `AssembleTurn`.
- Fakes (`Providers/.../Fakes/`) — let the STT fake yield a `SttFinal` with a `DetectedLanguage` (for orchestrator tests).
- Test files (provider-mapping + orchestrator + ws-mapping + start-validation suites).

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2)
**Slice 1 — detection capture (provider contract):**
1. **`DeepgramMapping_dominant_language_from_Languages`** — `Alternative.Languages=["es"]` ⇒ `SttFinal.DetectedLanguage == Es`.
2. **`DeepgramMapping_falls_back_to_word_language_mode`** — `Languages` empty, `Words` languages `[es,es,en]` ⇒ `Es`.
3. **`DeepgramMapping_unknown_or_absent_language_null`** — `["fr"]` / none ⇒ `null`.
4. **`SttFinal_default_detectedlanguage_null`** — 2-arg construction ⇒ `DetectedLanguage == null` (back-compat).

**Slice 2 — orchestrator direction-flip:**
5. **`Bidirectional_flips_direction_from_detected`** — bidir on, detected `Es` ⇒ the `TranslationRequest` is `Source=Es,Target=En` and the TTS request targets `En` (voice via `VoiceByLanguage[en]`).
6. **`Bidirectional_emits_direction_event_before_translation`** — a `Direction{Es→En}` event precedes the target transcript.
7. **`Bidirectional_null_detection_falls_back_to_start_direction`** — detection null ⇒ uses `p.Direction`; no wrong-direction translation.
8. **`OneDirection_unchanged_regression`** — bidir off ⇒ no `Direction` event, uses `p.Direction` (byte-identical to today).
9. **`WsMapping_direction_event_maps_to_message`** — `Direction{En→Es}` ⇒ `{type:"direction", direction:{source:"en",target:"es"}}`.
10. **`AssembleTurn_folds_resolved_direction`** — a turn with a `Direction{Es→En}` event ⇒ persisted `turn.Direction == Es→En`.
11. **`StartValidation_accepts_bidirectional_flag`** — `bidirectional:true` parsed true; absent ⇒ false.

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** `SttFinal` +`DetectedLanguage` (provider contract, ARCH-012/App A); `CascadeStartParams` +`Bidirectional`; new `Direction` output event + the `direction` WS message (ARCH-009/011).
- **Orchestrator doc rows to write hot:** the `SttFinal` provider-contract row; the cascade WS `start`/`direction` message rows; the ARCH-002/003 Phase-J revision note.

## Things to flag at Step 2.5
1. **Dominant-language resolution** — `Languages[0]` (Deepgram's own utterance pick) vs the mode of `Words[].Language`? **Default vote: prefer `Languages[0]`, fall back to the word-language mode, then null.** Both are pure + unit-tested.
2. **TTS voice in bidir** — pass an EMPTY voice so `VoiceByLanguage[resolvedTarget]` drives it (the voice matches the spoken-to language), rather than the frame's single `TtsVoice`? **Default vote: yes, ignore the frame's single voice in bidir; resolve by target language.** ⚠️ Requires `VoiceByLanguage` configured for BOTH `en`+`es` — if absent it falls back to the default voice (mono-voice, acceptable degrade). Flag at Step 9 if config lacks one (operational).
3. **Per-segment vs turn-level direction** — a turn with multiple SttFinal segments in different languages: translate each segment in its own resolved direction, turn-level `Direction` = the first resolved? **Default vote: per-segment translation (the loop already runs per-segment), turn `Direction` = first resolved segment.** (Two-people-take-turns is single-direction per turn anyway.)
4. **Does `language=multi` already feed `Alternative.Languages`?** Confirm the live request sets `language=multi` (it does via `SttLanguage="multi"`); the fields are empty otherwise. **Default vote: rely on the existing `multi` request; null-tolerate.**

## Dependencies + sequencing
- **Depends on:** nothing external (SDK verified). Lands in the Phase-J round.
- **Blocks:** FE-080 consumes the `{type:"direction"}` message + the `bidirectional` start flag; FE-081 renders the resolved per-turn direction.

## Estimated commit count
**2.** (1) detection capture — `SttFinal.DetectedLanguage` + Deepgram mapping + fakes; (2) orchestrator direction-flip + `Direction` event + ws-mapping/AssembleTurn fold + start-validation. Split keeps the `SttFinal` contract change bisectable. **No safety-invariant touch** (the `bidirectional` flag is an additive bool through `CascadeStartValidation`, not path-derivation/upload-validation; `Direction`/`DetectedLanguage` are language codes — no audio/secrets) → **no security-reviewer pass** (per the 2026-05-31 process change: per-slice reviewers disabled; the Step-7 invariant TESTS — `SessionPersistenceTests` sentinel et al. — are the gate).

## Lessons-logged candidates anticipated
- **Convention candidate** — riding a provider's typed multilingual signal (`Alternative.Languages`/`Word.Language`) for dynamic direction (contrast §19's SDK-hidden-field case — here the SDK exposes it; verified by reflection, not docs).
- **Architecture-doc note candidate** — ARCH-011/012: per-utterance dynamic direction (detected→other) as an additive cascade capability; one-direction preserved.
