# /tdd brief — cascade_turn_correctness (G.4; bug-6 cost close + the false-"failed" fix)

> **Backend slice (`server/`).** Two coupled cascade-turn-finalization bugs from the live both-modes smoke (repro
> artifact in hand — capture #1 FULFILLED). Deliverable-critical: a false-`failed` + a null cost poison the
> comparison's status, error-count, AND cost axes. No safety invariant (metrics/cost/status correctness).

## Feature
Fix two coupled cascade bugs proven by a persisted single-turn artifact: **(B)** a fully-successful cascade turn
(final translation + completed TTS) is wrongly persisted `status:"failed"` because a late, non-fatal `stt.unknown`
error (Deepgram teardown noise) is counted as fatal; **(A)** the turn's `costEstimate` is `null` despite the
translation usage being captured (49/29 tokens) — via the failed-status short-circuit AND missing TTS cost inputs
(`ttsVoiceUsed:""`, no TTS char-proxy).

## Use case + traceability
- **Task ID:** G.4 cascade-correctness (CLOSES bug-6 / cascade-cost; ADDS the false-`failed` Bug B). **Architecture:**
  ARCH-011/013 (cascade orchestrator + metrics/streaming honesty), ARCH-014 (cost), ARCH-005 (turn status).
- **Related:** lessons **§31** (052 — SKIP spurious empty STT finals; the two-flag terminal), **§30** (048 — spurious
  post-`Stop` Deepgram `stt.unknown` noise masquerading as a crash; **verify the error SOURCE**), **§9/§21/§23**
  (cost: branch-on-basis, char-proxy TTS, degrade-WHOLESALE), **§22** (fail-closed terminal).
- **⭐ Repro artifact (local disk; READ as the shape oracle — do NOT commit it; it's gitignored session data):**
  `data/sessions/session_20260531T184952Z_f5b47a83.json` (EN→ES, translation=gpt-5-nano). **Verified fields (orch
  confirmed via jq):** `turns[0].status:"failed"`, `errors:[{deepgram,stt,stt.unknown,retryable:false,httpStatusCode:null}]`,
  `transcripts[last]:{segmentId:"src-1",role:"source",text:"",isFinal:false}`, `latencyEvents` has
  `translation.final` meta `inputTokens:"49"/outputTokens:"29"` AND `tts.started`+`tts.first_audio`+`tts.complete`
  all present, `ttsVoiceUsed:""`, `costEstimate:null`. (The Debug log line 209 confirmed the live Responses `usage`
  shape: `{input_tokens:49, output_tokens:29, total_tokens:78}` — the translation parse WORKED, so the old
  "usage-shape mismatch" bug-6 hypothesis is DISPROVEN.)

## ⭐ Pre-orient at Step 1 (confirm the surfaces — grep located these)
- **Turn assembly + status derivation:** `Cascade/CascadeWsMapping.cs` (`AssembleTurn` — derives the turn's
  status + `errors[]` from the `CascadeOutputEvent` stream) + `Cascade/CascadeStreamingOrchestrator.cs` (where the
  trailing spurious `stt.unknown` is emitted — likely the §22/§30 fail-closed path firing on a LATE STT error AFTER
  the terminal `Done`). Confirm whether `status:failed` is `errors.Count>0` or a terminal `Done{failed}`.
- **Cost composite:** `Cost/CostEstimator.cs` (cascade estimate) + the cost fold in `CascadeWsMapping.cs`. Confirm
  whether costing is gated on non-failed status AND why the TTS leg degrades (`ttsVoiceUsed` empty + no char-proxy →
  composite WHOLESALE-null per §9/§21 even though the translation tokens are present).
- **`ttsVoiceUsed` + the TTS char count recording:** `Cascade/CascadeWebSocketEndpoint.cs` / `Sessions/SessionStore.cs`
  / `Sessions/SessionModels.cs`. The TTS stage RAN (events fired) but the voice wasn't propagated into the turn.

## Design (fix direction — honest, NO synthesis)
**Bug B — don't false-fail a successful turn.** A cascade turn that produced a final translation + a completed TTS
(`tts.complete`) is **`completed`**, NOT `failed`, even if a late non-fatal `stt.unknown` (+ the trailing empty
non-final segment) arrives after the terminal — that's the SAME benign post-success Deepgram teardown noise as §30
(verify the SOURCE; it's not a real failure). My lean (Q1): **suppress the spurious post-final STT event at the
orchestrator** (don't emit the error/empty-segment at all — cleanest, mirrors §31's skip; recording it ALSO poisons
`errorCount` + the comparison's error axis). Preserve the §22/§31 two-flag genuine-failure paths
(pure-silence→completed, all-empty→empty_transcript, real-error→failed).

**Bug A — populate cost honestly.** (A1) Fixing B (turn completed) lets costing run IF status-gated — confirm +
de-couple defensively (a completed turn with real usage MUST price). (A2) Record the REAL TTS cost inputs so the TTS
leg computes (no synthesis): `ttsVoiceUsed` from the actual TTS stage's resolved voice + the TTS char-proxy (§21 — the
target final text length). Today both absent → composite degrades wholesale (§9) → null despite translation 49/29.
Composite = translation (real tokens, additive) + TTS (real char-proxy); both present → non-null. Degrade to null
ONLY on a genuinely-absent real input (NEVER a synthetic $0 — §9/§25).

## Acceptance (RED-first; deterministic — scripted-events fakes + the artifact as the shape oracle)
- [ ] A cascade turn with a final translation + `tts.complete` + a TRAILING spurious `stt.unknown` (+ empty
      non-final segment) → status **`completed`**, `errorCount` **0** (spurious error suppressed/not-counted). RED via
      a scripted-events fake reproducing the artifact's sequence.
- [ ] The genuine-failure paths STILL fail (§22/§31 two-flag preserved): pin pure-silence→completed,
      all-empty→`empty_transcript`, a real mid-stream error→`failed`.
- [ ] The completed turn PRICES: `costEstimate` non-null, composed from the real translation tokens (49/29) + the
      real TTS char-proxy; `ttsVoiceUsed` recorded (non-empty). Pin cost-from-REAL-inputs (no synthetic $0; degrade to
      null only on a genuinely-absent input).
- [ ] `summary.cascade.errorCount` no longer counts the suppressed error; `estimatedCostPerMinuteUsd` populates.
- [ ] `/preflight` (backend) clean; full `dotnet test` green; build 0W/0E.

## Cross-doc invariant impact (flag at Step 9)
- Likely NONE if `ttsVoiceUsed`/a char-count field already exist on the turn model (just unpopulated). **IF a new
  turn-model field is needed (e.g. a TTS char count) → flag at Step 9** (Appendix A + cross-doc row). Confirm at Step 1.

## Things to flag at Step 2.5
1. **⭐ Bug-B suppression mechanism:** skip the spurious STT event at the orchestrator (my vote) vs status-derivation
   tolerance (a turn with final-translation+`tts.complete` is completed regardless). My vote: **skip at source** (also
   cleans `errorCount`).
2. **Cost de-couple from status:** is costing status-gated? de-couple defensively OR rely on the B fix? My vote: **both**.
3. **TTS char-proxy source:** the target final text length (my vote) vs a provider-reported char count.
4. **`ttsVoiceUsed` source:** the resolved TTS voice from config/options at the stage — confirm where it's available.
5. **New turn-model field needed?** (TTS char count) → if yes, cross-doc.

## Dependencies + sequencing
- **Depends on:** nothing (artifact + live usage shape in hand). **Blocks:** the G.5 write-up real cost numbers.
- **Pairs with:** a lead re-capture (a fresh cascade turn) to confirm cost populates + the turn reads `completed` live.

## Estimated commit count
**1–2.** Bug B (status/suppress-spurious) + Bug A (cost inputs) are the same cascade-finalization surface; may split
into 2 commits (status; cost) if they RED cleanly apart. No safety invariant.

## Lessons-logged candidates
- **Convention candidate** — "a cascade turn that produced a final translation + `tts.complete` is `completed`
  regardless of a late non-fatal STT error (extends §31's skip to the post-success teardown-noise error); cascade cost
  composes from real translation tokens + a real TTS char-proxy, never synthesized."
- **Architecture-doc note** — ARCH-013/014 (the cascade turn success + cost criteria).

## How to invoke
1. Read this brief + the repro artifact (local) + lessons §31/§30/§9/§21/§22.
2. Step 1: confirm the surfaces (`AssembleTurn` status derivation; the spurious-error emit-site; the cost composite;
   the `ttsVoiceUsed`/char-count recording).
3. `/tdd cascade_turn_correctness` — RED Bug B (the false-`failed` via a scripted-events fake reproducing the
   artifact) first, then Bug A (cost inputs).
4. Step 2.5: answer Q1–Q5. Step 9: categorized summary + any cross-doc flag + ship/no-ship + draft commit message.
