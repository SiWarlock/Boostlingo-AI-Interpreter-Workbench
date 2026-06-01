# Session 019 — Backend: H.3 persistence read-tier + cascade metrics/cost correctness

- **Date:** 2026-05-31
- **Phase:** H.3-backend (session-history read tier) + G.4 cascade-correctness (metrics/cost) + a history-UX enhancement
- **Area:** backend (`server/`)
- **Predecessor:** [016 — Backend: mode-switch / .env loader / cost-correctness arc / Phase-I cascade auto-VAD](016-2026-05-31-backend-cost-arc-and-phase-i-cascade-autovad.md)
- **Successor:** [021-2026-06-01-backend-bidirectional-and-autovad-empty-fix.md](021-2026-06-01-backend-bidirectional-and-autovad-empty-fix.md) (Phase-J bidirectional BE half — cascade detect+flip, realtime instruction — + the auto-VAD empty-silence smoke fix)

> Implementer session doc (technical close-out only). `MVP_TASKS.md` / `server/LESSONS.md` / `server/CLAUDE.md` / `ARCHITECTURE.md` are orchestrator-owned; the Step-9 items below are **surfaced** for `/orchestrate-end` (hot-routed during the session; the round-seal commits already landed — working tree clean). Cycling at a clean round boundary (fresh implementer pair for the bidirectional build).

## Why this session existed
Fresh backend implementer (predecessor cycled at ACTION). Ran the H.3 persistence read tier (so the history view lists past sessions across a restart) and the metrics/cost-accuracy round that the live both-modes smoke surfaced (a false-`failed` cascade turn + a null cost + a ≈0 TTS first-audio latency), plus a small history-UX enhancement. Five slices landed, all reviewer-clean.

## What was built (per slice, with verified commit hashes)

### 065 — H.3-backend session-history reader (`514ba45`)
The FIRST request-reachable disk-read tier for persisted sessions. New `SessionPersistenceReader` enumerates `SESSION_DATA_DIR` for `session_*.json` (`TopDirectoryOnly`), deserializes via `JsonDefaults`, degrades **per-file** (a corrupt/oversize/unreadable file is skipped, never blanks the list) with a single-open size guard (closes the stat→read TOCTOU). `GET /api/sessions` returns lightweight `SessionListItem` summaries (`{sessionId,label,startedAt,endedAt,turnCount,modes}`), most-recent-first; a misconfigured data dir → sanitized `sessions.read_failed`; a missing dir → empty list. **security-reviewer: invariants #1–#5 PASS; symlink-in-dataDir ruled out-of-threat-model (documented).**

### 068 — `GET /{id}` disk-fallback (`b0ce318`)
`SessionPersistenceReader.ReadById` (pre-FS `^[A-Za-z0-9_-]+$` gate reusing the writer's now-`internal` `IsValidSessionId`, then enumerate + match on the deserialized `SessionId`). `SessionService.Get` (and `Summary`) try in-memory first, disk second — so a past/evicted session returns 200 + full detail instead of 404. **security-reviewer: invariants PASS; pre-FS gate + evicted-mutation fail-close (no resurrection) explicitly confirmed.** Closed the 065 `GET /{id}`-fallback carry-forward.

### 069 — cascade-correctness: false-`failed` + null-cost (`db5afb6` Bug B, `33d024e` Bug A)
Verified against a live repro artifact. **Bug B:** a trailing spurious empty `SttPartial` (Deepgram teardown noise) set `pendingPartial` → stream-end §22 read it as a lost final → `stt.unknown` → `Done(Failed)`, false-failing a successful turn. Fix: skip empty/whitespace partials (extends §31's empty-FINAL skip one level up). **Bug A:** `gpt-4o-mini-tts` bills on `audio_output_tokens` but `ComputeCost` supplied only a char-proxy → composite degraded wholesale to null. Context7-confirmed `/v1/audio/speech` reports no usable usage + no per-char rate → estimate output-audio minutes from the target text length (`TtsApproxCharsPerMinute=900`) feeding `approxUsdPerAudioMinute`; `EstimateCascadeTurn` now merges stage assumptions so the cascade-ESTIMATED vs realtime-EXACT asymmetry is honest in the persisted data (G.5). Also resolve `ttsVoiceUsed` from session config when the frame omits it. **security-reviewer: 5/5 PASS, no regression.**

### 075 — cascade `tts.started` at TTS request-initiation (`8dee6d7`) — supersedes 057c
`TtsFirstAudioMs` read ≈0 because `tts.started` was stamped on the provider's first event (which already absorbs the request→provider round-trip). Re-anchored to TTS request-initiation (a real moment we control, via the unchanged `IClock`/`LatencyEventFactory` chain) → a genuine round-trip-inclusive latency; `tts.first_audio` still on the real provider event. **Mandatory security-reviewer honesty pass: PASS on every axis** (not back-dated/synthesized/relabeled).

### 077 — auto-derive session label (`271adea`)
A blank `Label` is auto-derived at end-persist from the first source-final transcript snippet (≤40 chars + "…"), with a mode+direction fallback (`Cascade · EN→ES`); a user-typed label wins. Pure `SessionLabelDeriver` applied in `EndAsync`; written to the store via new `SessionStore.SetLabel` (mirrors `SetSummary`) so `GET /{id}` + the persisted file agree; flows to `GET /api/sessions` via the existing `SessionListItem.Label`. No FE change. **security-reviewer (light sanity): PASS, no new exposure** (label is a substring of already-persisted transcript text).

### Files created
- `Sessions/SessionPersistenceReader.cs` (065; +`ReadById` 068) — the persisted-session disk read tier.
- `Sessions/SessionListItem.cs` (065) — the lightweight history-list summary DTO + `FromSession`.
- `Sessions/SessionLabelDeriver.cs` (077) — the pure blank-label deriver.
- Tests: `SessionPersistenceReaderTests.cs` (065/068), `SessionLabelDeriverTests.cs` (077).

### Files modified
- `Sessions/SessionService.cs` — `ListPersistedSessions` (065); `Get`/`Summary` in-memory→disk fallback (068); `EndAsync` label derive (077).
- `Sessions/SessionPersistenceWriter.cs` — `IsValidSessionId` `private`→`internal` (068, reused by the reader).
- `Sessions/SessionStore.cs` — `+SetLabel` (077, mirrors `SetSummary`).
- `Controllers/SessionsController.cs` — `GET /api/sessions` action (065).
- `Security/ErrorSanitizer.cs` — `sessions.read_failed` safe message (065).
- `Program.cs` — `SessionPersistenceReader` singleton + `SESSION_MAX_READ_BYTES` (065).
- `Cascade/CascadeStreamingOrchestrator.cs` — skip empty `SttPartial` (069); `tts.started` at initiation + reframed debug log (075).
- `Cascade/CascadeWsMapping.cs` — `BuildTtsCostUsage`, `ResolveTtsVoice`, `TtsApproxCharsPerMinute` (069).
- `Cascade/CascadeWebSocketEndpoint.cs` — `ComputeCost` uses `BuildTtsCostUsage`; `RunTurnAsync` resolves the voice (069).
- `Cost/CostEstimator.cs` — `EstimateCascadeTurn` merges stage assumptions + enriched approx-minute wording (069).
- Tests: `SessionsControllerTests.cs`, `CascadeOrchestratorTests.cs`, `CostEstimatorTests.cs`, `CascadeWebSocketTests.cs`; ctor ripples in `RealtimeTurnCostTests.cs` + `StaleSessionFlushTests.cs` (065, the reader DI dep).

## Decisions made
- **065:** lightweight `SessionListItem` (payload hygiene) over full `InterpretationSession[]`; degrade-per-file; `StartedAt` desc with a `SessionId` tiebreak (reviewer fix); single-open size guard (closes the TOCTOU, reviewer fix); `ReadById` deferred to 068.
- **068:** transparent in-memory→disk fallback in `Get` (controller byte-identical); `ReadById` returns `InterpretationSession?` (null collapses not-found/invalid/missing/corrupt/misconfig); `Summary` routes through `Get` too (reviewer-converged fix — `GET /{id}/summary` now reads disk, matching the stated intent); evicted-session mutation fail-closes (pinned).
- **069:** skip empty partials at the orchestrator (mirrors §31); char→minutes TTS estimate for the audio-token-basis model (Context7-grounded); resolve `ttsVoiceUsed` from config; cost is computed regardless of status (disproved the brief's "failed-status short-circuit" hypothesis).
- **075:** anchor `tts.started` at initiation (option a); kept `logger` with a reframed real-latency debug log (avoids orphaning the primary-ctor param + a `CascadeBlobTests` ripple outside the file set).
- **077:** apply at `EndAsync` (canonical finalize) + a small `SessionStore.SetLabel` for in-memory/persist agreement; `"source"` role literal; 40-char hard cut; mode+direction fallback (Realtime before Cascade; turnless → `CurrentMode`).

## Decisions explicitly NOT made (deferred)
- The **bidirectional EN↔ES** backend half (next BE slice; orchestrator scoping the two-sessions-vs-per-turn-switch sub-decision).
- The **blob fallback** (`CascadeOrchestrator`) TTS cost still uses `Characters`-only — out of 069's WS-turn scope, **zero behavioral effect** (the blob composite is already null via §23's no-blob-STT-pricing); a latent-consistency follow-up.
- A **transcript-role-constant canonicalization** (the `"source"` literal is duplicated across `SessionLabelDeriver` + cascade `RoleSource` + FE `realtimeEventSink`) — opportunistic.

## TDD compliance
**Clean.** Every slice was RED-first (compile-RED on a missing symbol, or behavioral-RED against the old code — e.g. 069 Bug B Failed-vs-Completed, 075 ≈0-vs-300ms). Reviewer-driven fix-in-slice additions were either test-strengthenings or RED-first. **Exempt (per the project ARCH-020 WS-shell posture):** the WS endpoint transport glue (069 `ComputeCost`/voice wiring) is manual-smoke; its pure decision logic (`BuildTtsCostUsage`, `ResolveTtsVoice`, the orchestrator skips/stamps, the deriver) is unit-pinned.

## Cross-doc invariant audit
- **065 — the only cross-doc invariant CHANGE this session:** new `GET /api/sessions` endpoint + new `SessionListItem` DTO → ARCH-009 + Appendix A + the server cross-doc table; plus ARCH-016 read-tier note, `sessions.read_failed` (ARCH-018), `SESSION_MAX_READ_BYTES` (ARCH-028). Flagged at Step 9; orchestrator-written (hot-routed; round-seals landed — working tree clean).
- **068/069/075/077:** NO model/DTO/wire field changes (behavior + instrumentation only) → no cross-doc table rows. ARCH-013/014/016/017 realization notes only (orchestrator folds at round-seal). No drift.

## Reachability
- **065** `GET /api/sessions` → `SessionsController.List` → `ISessionService.ListPersistedSessions` → `SessionPersistenceReader.ReadAll` (DI singleton). E2E-proven.
- **068** `GET /{id}` (+`/summary`) → `Get`/`Summary` → `_store.Get ?? _reader.ReadById`. E2E-proven (evicted→disk).
- **069** `WS /api/cascade/stream` → `orchestrator.RunAsync` (Bug B) + `EmitTerminalAsync→ComputeCost→BuildTtsCostUsage→EstimateCascadeTurn` (Bug A). Pure logic unit-pinned; WS glue manual-smoke.
- **075** `WS /api/cascade/stream` → `orchestrator.RunAsync` → TTS stage. Unit-pinned.
- **077** `POST …/end` → `EndAsync` → `SetLabel`/persist; flows to `GET /api/sessions` + `GET /{id}` + the file (D1 proves all three).
- **No tested-but-unwired gaps.**

## Open follow-ups (Step-9 categorized — surfaced for `/orchestrate-end`; orchestrator routes, NOT re-routed here)
**Cross-doc invariant change (orchestrator writes):** 065 — `GET /api/sessions` + `SessionListItem` → ARCH-009 + Appendix A + server cross-doc table.
**Architecture-doc notes:** ARCH-016 read-tier + by-id read-tier (065/068); ARCH-009 `GET /{id}`(+`/summary`) disk-fallback behavior (068); ARCH-013/014 cascade turn-success + cost-is-disclosed-estimate (069); ARCH-013 `tts.started`-at-initiation (075, **supersedes 057c**); ARCH-016/017 auto-derived label (077).
**Lesson candidates (orchestrator banks):** §35 persisted-session disk reader (065) + its by-id amendment (068); empty-PARTIAL skip extends §31 + cascade TTS char→minutes disclosed-estimate + config-voice-resolution (069); stage-START stamped at INITIATION not the provider's first response event (075).
**Future TODO — G-hardening / opportunistic:** blob-path TTS cost consistency (069, zero effect today); transcript-role-constant canonicalization (077); 065 deferred LOWs (no `maxFiles` count-cap; `sessions.read_failed` LogError-vs-Warning); test-polish (068 RB2 clarity; cross-test-file fixture dedup; 069 Bug-B test wording).
**057c supersession (for the record):** the provisional 057c "no fix / provider artifact" decision is superseded by 075 — the smoke confirmed ≈0, the delta is now the metric.
