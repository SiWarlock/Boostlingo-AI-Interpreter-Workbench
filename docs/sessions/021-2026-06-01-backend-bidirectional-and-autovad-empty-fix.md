# Session 021 — Backend Phase-J bidirectional (cascade + realtime) + auto-VAD empty-silence fix

- **Date:** 2026-06-01
- **Phase:** J (Bidirectional conversation) — backend half + the Phase-J smoke Finding-1 BE fix
- **Role:** backend implementer (`boostlingo-main-backend-implementer`)
- **Predecessor:** [019-2026-05-31-backend-h3-read-tier-and-cascade-correctness.md](019-2026-05-31-backend-h3-read-tier-and-cascade-correctness.md) (prior BE impl session; the concurrent orch handoff that framed Phase J is [020](020-2026-05-31-orch-handoff-metrics-seal-bidirectional.md))
- **Successor:** _(next backend session — G.4 synthetic soak harness)_

## Why this session existed

Phase J makes the workbench bidirectional within one live session: per utterance, auto-detect the spoken language → translate to the OTHER language → emit on VAD speaker-stop. The user (via the lead's `AskUserQuestion`) finalized the design (`docs/bidirectional-phase-design.md`): ride Deepgram's streaming detection (Option A), bundle both modes in one round, single chronological transcript stream. This session built the **backend half** (cascade detection + direction-flip; realtime instruction) and then fixed the **first smoke Finding** — auto-VAD empty turns false-failing.

## What was built

### 078 — cascade bidirectional detection (J.1) · `3bd8306` (detect) + `33726ec` (flip)

**Files modified:**
- `Providers/Abstractions/ProviderEvents.cs` — `SttFinal` gained `LanguageCode? DetectedLanguage = null` (trailing-defaulted; back-compat).
- `Providers/Deepgram/DeepgramSttMapping.cs` — `ToSttEvent` populates `DetectedLanguage`; new `ResolveDetectedLanguage`/`NormalizeLanguage` helpers. Dominant = the alternative's utterance pick (`Languages[0]`, normalized `en*`→En/`es*`→Es); fallback = the MODE of per-word `Language` tags, **null on a genuine tie** (ambiguous → orchestrator keeps the start direction); else null. Rides the SDK's typed `Alternative.Languages`/`Word.Language` (verified by reflection — lesson §19 contrast: here the SDK *exposes* the fields).
- `Cascade/CascadeModels.cs` — `CascadeStartParams` gained `bool Bidirectional = false`; new `Direction(LanguageDirection Resolved)` output event.
- `Cascade/CascadeStartValidation.cs` — parses `bidirectional` (bool, default false).
- `Cascade/CascadeStreamingOrchestrator.cs` — per-segment `ResolveDirection` (detected→other under bidir; else start-frame direction); emits `Direction` before translation (bidir only); `Translate/SynthesizeSegmentAsync` take the resolved `LanguageDirection`; bidir passes an empty TTS voice so the provider resolves by target language.
- `Cascade/CascadeWsMapping.cs` — `Direction`→`{type:"direction", direction:{source,target}}`; `AssembleTurn` folds the first `Direction` event into `turn.Direction`.
- Tests: `DeepgramSttProviderTests.cs` (5), `CascadeOrchestratorTests.cs` (4 + recording fakes), `CascadeWebSocketTests.cs` (4), `CascadeStartValidationTests.cs` (1).

### 079 — realtime bidirectional instruction (J.2) · `574ad19`

**Files modified:**
- `Realtime/RealtimeModels.cs` — `RealtimeTokenRequest` gained `bool Bidirectional = false` (`Direction` preserved as the turn's initial/fallback direction).
- `Realtime/RealtimeClientSecretMapping.cs` — `DefaultBidirectionalInstructionsTemplate` const (detect-EN/ES → render-the-OTHER); `RenderInstructions` + `BuildRequestBody` gained `bool bidirectional` threaded into `session.instructions`.
- `Realtime/RealtimeClientSecretService.cs` — threads `request.Bidirectional` → `SendWithRetryAsync` → `BuildMessage` → `BuildRequestBody`.
- Tests: `RealtimeClientSecretMappingTests.cs` (4, Group 4).

### 083 — auto-VAD empty turn = silence, not failed (J.6, smoke Finding 1) · `cbec833` (scoping) + `76e2852` (summary exclusion)

**Files modified:**
- `Cascade/CascadeStreamingOrchestrator.cs` — `TerminalFailure()` empty_transcript arm scoped `&& !p.AutoVad`: an auto-VAD turn with empty-only finals (a silent gap in the J.5 continuous loop) Completes-silence instead of false-failing `cascade.empty_transcript`. Manual empty stays failed; the `pendingPartial`→`stt.unknown` (lost-final) arm stays for BOTH modes.
- `Sessions/SessionSummaryService.cs` — `SummarizeMode` excludes Completed-0-transcript turns via new `IsEmptySilence` (Completed-scoped, so a failed-empty turn's error still surfaces).
- Tests: `CascadeOrchestratorTests.cs` (6 new + 1 existing test updated to the new contract); `SessionSummaryServiceTests.cs` (1 new + the `Turn` helper now carries a content transcript for realistic fixtures).

## Decisions made

- **Cascade dominant-language resolution (078):** prefer `Languages[0]`; fall back to the per-word mode on an *unrecognized* utterance language (not only an empty list) — uses all signal; still null when there's no EN/ES signal. A genuine word-mode **tie → null** (ambiguous → fall back, not a coin-flip — code-quality review finding, fixed in-slice).
- **Bidir TTS voice (078):** in bidir the orchestrator passes an empty voice so `OpenAiTtsMapping.ResolveVoice` resolves by the resolved target language (`VoiceByLanguage[target]`); one-direction preserves the frame voice.
- **`Direction` event emission (078):** emitted whenever bidir is on (detected or fallback) — uniform; on fallback it equals the start-frame direction so the FE stamps the same thing. Type named `Direction`, property `Resolved` (C# forbids a positional property matching its enclosing type).
- **Realtime bidir template const-only (079):** no `RealtimeOptions.BidirectionalInstructionsTemplate` override (the one-direction `InstructionsTemplate` override exists but nothing sets it — YAGNI).
- **083 Q2 exclusion scoped to Completed (refinement):** `Status==Completed && Transcripts.Count==0`, NOT a blanket `Transcripts.Count==0` — a Failed-early turn (0 transcripts + a real error) is KEPT so its error isn't hidden from `errorCount` (honors the brief's own Q2 caveat).

## Decisions explicitly NOT made (deferred)

- **`TtsVoiceUsed` per-direction accuracy in bidir** — the persisted `TtsVoiceUsed` reflects the config voice, not the per-segment resolved voice. Carry-forward (next-brief working set).
- **`.Take(N)` cap on the `ResolveDetectedLanguage` Words LINQ** — defense-in-depth on an SDK-materialized list; security reviewer said defer (no genuine DoS; `Languages` is the primary path). Deferred-hardening backlog.
- **`RealtimeOptions.BidirectionalInstructionsTemplate` override** — only if a real need appears. Carry-forward (opportunistic).

## TDD compliance

**Clean.** Every code change was RED-first:
- 078/079: compile-error RED on the new symbols (`SttFinal.DetectedLanguage`, `CascadeStartParams.Bidirectional`, `Direction`, `RealtimeTokenRequest.Bidirectional`, the `bidirectional` params), confirmed before GREEN.
- 083: runtime-assertion RED on tests 1 + 6 (Expected Completed/2, Actual Failed/3), confirmed before GREEN; tests 2–5 are regression-preservation pins (green-already, intentional).
- One existing test (`auto_vad_utterance_end_after_only_empty_finals_fails_empty_transcript`) encoded the OLD §31-on-auto-VAD contract that J.6 supersedes — updated in place (renamed `…_completes_silence`, assertion flipped WITH the contract, J.6-commented). Not a TDD violation: the implementation matched the brief; the old test pinned the now-superseded behavior.

## Reachability (Step 7.5)

- **078 bidirectional flag** — reachable from `CascadeWebSocketEndpoint.HandleAsync` (cascade WS route): start frame → `CascadeStartValidation.ParseStart` → `CascadeStartParams.Bidirectional` → orchestrator. **`Direction` event** → `CascadeWsMapping.ToServerMessage` (the `{type:"direction"}` wire message) + collected into `AssembleTurn` (persisted-turn fold). Blob path stays one-direction (flag defaults false).
- **079 realtime bidirectional** — reachable from `POST /api/realtime/client-secret` (`RealtimeController.Mint`) → `MintAsync` → `SendWithRetryAsync(…, request.Bidirectional)` → `BuildMessage` → `BuildRequestBody` → `RenderInstructions`. `[FromBody]` auto-binds the new field (no controller edit).
- **083 scoping** — reachable from the auto-VAD cascade WS path (`p.AutoVad` from the start frame) + the blob path. **Summary exclusion** — reachable from `SessionSummaryService.Compute` (`GET …/summary` + the `/end` snapshot).

No tested-but-unwired gaps. FE-080/081/084 consume these contracts (FE implementer's halves).

## Open follow-ups (Step-9 categorized list — orchestrator routes/verifies at `/orchestrate-end`)

**Cross-doc invariant changes** (orchestrator writes the rows + ARCHITECTURE.md edits; flagged at each Step 9):
- `SttFinal` +`DetectedLanguage` → ARCH-012 / Appendix A (provider contract).
- `CascadeStartParams` +`Bidirectional` + the WS `start` `bidirectional` field → ARCH-009/011.
- New `Direction` cascade output event → new WS message `{type:"direction", direction:{source,target}}` → ARCH-009/011.
- `RealtimeTokenRequest` +`Bidirectional` + the bidir instruction template → ARCH-009/010.
- ARCH-002/003 Phase-J revision note (additive bidirectional; one-direction preserved).
- ARCH-011/013 note: auto-VAD empty = silence→Completed; 0-transcript Completed turns excluded from `ModeSummary`.
- ⭐ No `BuildFullSession` persistence-sentinel change needed (§29 meta-rule): `SttFinal` is a provider event (not persisted); `Direction` folds into the EXISTING `InterpretationTurn.Direction` — no new persisted field.

**Convention candidates (lessons — orchestrator writes `server/LESSONS.md`):**
- Riding a provider's TYPED multilingual signal (`Alternative.Languages`/`Word.Language`) for dynamic direction (contrast §19's SDK-hidden case; verified by reflection). Resolution = utterance-pick → word-mode-null-on-tie → null.
- The empty-final lineage extended: deliberate-manual-silence FAILS (`empty_transcript`); VAD-gap-silence COMPLETES; the per-mode comparison excludes 0-transcript **Completed** turns but KEEPS failed-empty (so a real early-failure error isn't hidden). Extends §52/§69/§31/§22 + the §29 exclusion pattern.

**Future TODO — next-brief working set (Carry-forward):**
- `TtsVoiceUsed`-in-bidir observability gap (persisted reflects config voice, not the per-direction resolved voice).
- Realtime bidir-template override asymmetry (`RealtimeOptions.BidirectionalInstructionsTemplate` only if a real need appears).

**Future TODO — deferred hardening backlog:**
- `.Take(N)` cap on the `ResolveDetectedLanguage` Words LINQ (defense-in-depth; SDK-materialized, `Languages`-primary).

**Operational:**
- Bidir TTS-by-target needs `VoiceByLanguage` configured for BOTH `en`+`es`; a missing language degrades to the default voice (mono-voice — acceptable). Config/build-confirm.

## How to use what was built

A cascade turn opened with `{"bidirectional":true}` on the WS `start` frame now detects each utterance's language and translates detected→other, emitting `{type:"direction"}` so the FE can stamp the live turn; a realtime mint with `{"bidirectional":true}` renders the detect-and-render-OTHER instruction. Auto-VAD continuous turns that catch silence no longer pollute the comparison with false errors.
