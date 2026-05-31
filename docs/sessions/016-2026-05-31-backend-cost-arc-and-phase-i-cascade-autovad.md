# Session 016 — Backend: mode-switch, .env loader, cost-correctness arc, Phase-I cascade auto-VAD

- **Date:** 2026-05-31
- **Phase:** Finding-2c (mode-switch) + G.2b (.env) + G.4 (cost-correctness arc) + 053-C2a (realtime cost) + Phase I (auto-VAD, cascade half)
- **Area:** backend (`server/`)
- **Predecessor:** [013 — Real-key-smoke backend bug-fix cycle (G.4)](013-2026-05-31-smoke-bugfix-backend.md)
- **Successor:** _(next BE session — picks up H.3-backend or the (B) turn-control-mode persistence; TBD)_

> Implementer session doc (technical close-out only). `MVP_TASKS.md` / `server/LESSONS.md` / `server/CLAUDE.md` / `ARCHITECTURE.md` are orchestrator-owned and untouched here. Cross-doc + lesson items are **surfaced** below for `/orchestrate-end` (the orchestrator is hot-routing them — `ARCHITECTURE.md`/`server/CLAUDE.md`/`server/LESSONS.md` show as modified in the working tree). Round-seal `a0b09c3` already routed the earlier slices (050/055); the 057/059/062 round is still OPEN.

## Why this session existed

Continuation of the real-key-smoke bug-fix arc (wholesale cycle from handoff 004), then the cost/metrics-correctness work and the start of Phase I. The live smoke surfaced backend integration bugs invisible to fake tests (a stale mode after a mid-session toggle, the cascade cost `n/a` on a successful turn, a session-avg responsiveness anchor that disagreed with the frontend's per-turn re-anchor, realtime cost never priced), plus dev-ergonomics friction (manual `.env` sourcing) and the approved Phase-I auto-VAD work. Five backend slices landed.

## What was built

### Earlier this session — round sealed at `a0b09c3`
- **050-backend** (`2ff1604`) — `POST /api/sessions/{id}/mode` mode-switch endpoint (Finding 2c): validates the target mode against the enum allowlist (off-enum/blank → sanitized `400 session.invalid_mode`; unknown id → `404 session.not_found`), atomically updates `config.CurrentMode` AND records a server-derived `ModeTransitionEvent` via a new atomic re-entrant `SessionStore.SwitchMode` wrapping the now-wired `RecordModeTransition`. Fixes a turn created after a mid-session switch being stamped with the stale mode.
- **055 G.2b** (`6ceee36`) — `Config/DotEnvLoader.cs` auto-loads the repo-root `.env` at startup (fill-gaps, degrade-if-absent, walk-up from `AppContext.BaseDirectory`), **gated out of the test host** (`ShouldAutoLoad` entry-assembly predicate) so a dev `.env` can't flip provider DI from fakes to real under `WebApplicationFactory`.

### This round (OPEN — 057/059/062)
- **057a** (`0ac73fb`) — re-anchored `MetricsAggregator.Compute`'s universal first-audio metric to `Get(SttFinal) ?? recordingStopped` (cascade gets stt.final; realtime falls back unchanged — no mode-branch), so the session-average agrees with 056-c1's frontend per-turn re-anchor. Fully closed on fakes.
- **057b** (`b14657b`) — made `OpenAiTranslationMapping.ReadUsage` a tolerant multi-shape parser (`response.usage` + top-level `usage`) + added a Debug-level, `IsEnabled`-guarded, sanitized raw-usage diagnostic in the translation provider. **NOT closed** — Context7 confirmed the documented shape already matched the prior parser, so the live cost `n/a` is likely usage-absent-from-stream; the debug log confirms the real shape on the lead's live cascade turn.
- **057c** (`dfbe79f`) — instrument-only: a Debug log of the `tts.first_audio − tts.started` delta in the orchestrator + a stamp-pair guard test (the live 0 ms is a provider-synchronous-yield artifact, not a stamping bug). No speculative fix.
- **053-C2a / 059** (`2977f7f`) — `CompleteTurnRequest` gains 3 trailing-optional audio-token fields (`InputAudioTokens`/`OutputAudioTokens`/`CachedAudioInputTokens`); `EstimateRealtime` prices realtime turns from the DC's **exact** `response.done.usage` audio-token counts (fixture → 0.004448 USD) instead of the audio-seconds×50 estimate, keeping the estimate as the absent-tokens fallback; text tokens disclosed-unpriced (no text rates configured). Closes the E.2b played-duration carry-forward. The FE producer (053-C2b) landed in the FE session (`8acaf58`).
- **062 / I.1** (`943089e` + `70a022f`) — Phase-I cascade auto-finalize. New `SttUtteranceEnd` provider event surfaced from Deepgram's `UtteranceEndResponse` (subscribe + `ToUtteranceEnd` map + `vad_events=true` schema); the orchestrator auto-finalizes the cascade turn on detected silence when a per-turn `autoVad` WS-start flag is set, routing through the SAME idempotent `FinalizeTurn` seam + the SAME `TerminalFailure()` §31/§22 two-flag (extracted, shared with stream-end). A WS pump-cancel fix (`pumpCts`) prevents an `await pump` hang when the auto-finalize emits `Done` before any stop frame. Manual mode unchanged (REVISES ARCH-003).

### Files created
- `server/AiInterpreter.Api/Config/DotEnvLoader.cs` (055) — the `.env` auto-loader (gated, fill-gaps, degrade).
- `server/AiInterpreter.Tests/DotEnvLoaderTests.cs` (055) — loader + gate-predicate tests.

### Files modified (this round, 057/059/062)
- `Metrics/MetricsAggregator.cs` (057a) — first-audio anchor `stt.final ?? recording.stopped`.
- `Providers/OpenAI/OpenAiTranslationMapping.cs` + `OpenAiTranslationProvider.cs` (057b) — tolerant `ReadUsage` + `DescribeUsageShape` + the IsEnabled-guarded sanitized log.
- `Cost/CostModels.cs` + `Cost/CostEstimator.cs` + `Sessions/SessionDtos.cs` + `Sessions/SessionService.cs` (059) — exact-count realtime pricing + the `CompleteTurnRequest` token fields + the service threading (with a documented request↔CostUsage naming-flip signpost).
- `Cascade/CascadeStreamingOrchestrator.cs` (057c tts-delta log + 062 auto-finalize/`TerminalFailure`), `Cascade/CascadeModels.cs` + `Cascade/CascadeStartValidation.cs` (062 `autoVad`), `Cascade/CascadeWebSocketEndpoint.cs` (062 pump-cancel).
- `Providers/Abstractions/ProviderEvents.cs` (062 `SttUtteranceEnd`), `Providers/Deepgram/DeepgramSttMapping.cs` + `DeepgramSttProvider.cs` (062 UtteranceEnd map/subscribe/schema).
- Tests: `MetricsAggregatorTests`, `SessionSummaryServiceTests`, `OpenAiTranslationProviderTests`, `CostEstimatorTests`, `RealtimeTurnCostTests`, `CascadeOrchestratorTests`, `CascadeBlobTests`, `DeepgramSttProviderTests`, `CascadeStartValidationTests`.

## Decisions made
- **050:** raw-string `SetModeRequest` (not the enum) so an off-enum value rejects as a sanitized 400 at the service chokepoint (lesson §27), not a framework ProblemDetails; atomic re-entrant `SwitchMode` wraps `RecordModeTransition`; no persist at `/mode` (rides the next `/complete`/`/end`).
- **055:** entry-assembly test-host gate over a ModuleInitializer sentinel (self-contained); hand-rolled parser over DotNetEnv (no dep; the inline-comment case is the 051-class risk, pinned by tests).
- **057b:** deferred to the Context7 evidence over the brief's nesting hypothesis (documented shape matched) → the tolerant parser is defensive insurance; the raw-usage log is the load-bearing diagnostic. Did NOT close bug-6 on fakes.
- **059:** exact-count + keep the seconds estimate as the fallback; text tokens disclosed-unpriced (no config rates); reused `CostUsage.AudioOutputTokens` (TTS) for realtime output + a deliberate request↔CostUsage naming flip with a signpost comment.
- **062:** fallback turn-model (auto-finalize the whole turn on the FIRST utterance-end — backend-only) over per-utterance turns (FE-coordinated); a typed `SttUtteranceEnd` variant over overloading `SttFinal`; the WS pump on its own linked CTS so the finally can cancel it (the `Done`-before-stop park-hazard).

## Decisions explicitly NOT made (deferred)
- **bug-6 (057b) is NOT closed** — awaits the lead's live cascade turn + the new debug log to confirm the real usage shape; if usage is genuinely absent, the real fix is a request-body usage opt-in (a follow-up slice), NOT the parser change.
- **062 — the UtteranceEnd subscribe + `vad_events=true` are UNCONDITIONAL** (the provider can't see the per-turn `AutoVad`, an orchestrator concern): in manual mode Deepgram emits UtteranceEnd → `SttUtteranceEnd` written to the channel → orchestrator ignores it. Functionally correct + documented in-code; a future gate plumbs `AutoVad` into `SttRequest`/the schema (G-hardening).
- **Per-utterance cascade turns** (one `InterpretationTurn` per utterance) — FE-coordinated (a cascade-FE turn-manager + 063); origin 062.
- The cascade-FE Stop-optional UI + the (B) turn-control-mode persistence (orchestrator's slice).

## TDD compliance
**Clean.** Every deterministic change was RED-first (compile-RED on the missing symbol/assertion, then GREEN): 050 (store/controller tests), 055 (loader + gate-predicate), 057a (re-pin + guards), 057b (`ParseEvent` multi-shape), 059 (exact-count + service-threading), 062 (orchestrator auto-finalize + mapper + parse, incl. the reviewer-driven §22 dangling-partial fix-in-slice). **Exempt (per the root TDD posture):** the 057c tts-delta log + the 057b raw-usage log are instrumentation (no behavior test); the 062 WS `pumpCts.Cancel()` + the Deepgram `UtteranceEndResponse` subscribe ride the manual-smoke WS transport shell (no socket harness — consistent with the project's ARCH-020 WS-shell posture; exercised by the live auto-VAD smoke).

## Reachability
- **050** — `POST /api/sessions/{id}/mode` → `SessionsController.SwitchMode` → `ISessionService.SwitchMode` → `SessionStore.SwitchMode`/`RecordModeTransition`. ✓
- **055** — `Program.cs` → `DotEnvLoader.AutoLoad(AppContext.BaseDirectory)` (gated) before `CreateBuilder`. ✓
- **057a** — `GET /sessions/{id}/summary` + `/end` → `SessionSummaryService` → `MetricsAggregator.Compute`. ✓
- **057b/c** — `WS /api/cascade/stream` → orchestrator translation/TTS stages (the diagnostics are on this path; live-gated for the bug-6 confirm). ✓
- **059** — `POST …/turns/{turnId}/complete` → `CompleteTurnAsync` → `EstimateRealtimeCost` → `EstimateRealtime` (exact-count). The FE producer is 053-C2b (landed). ✓
- **062** — `WS /api/cascade/stream` → `RunTurnAsync` → `orchestrator.RunAsync` (with `p.AutoVad`) → on `SttUtteranceEnd` → terminal `Done` → `EmitTerminalAsync` → `FinalizeTurn`. The Deepgram subscribe + WS pump-cancel are manual-smoke transport (reachable via the WS endpoint; live-smoke-verified). ✓
- **No tested-but-unwired gaps.**

## Open follow-ups (Step-9 categorized list — surfaced for `/orchestrate-end`)
**Cross-doc invariant changes (orchestrator writes; round OPEN for 057/059/062):**
- 050 (sealed `a0b09c3`): new ARCH-009 `POST /sessions/{id}/mode` route + Appendix A `SetModeRequest`; ModeTransitionEvent graduation (ARCH-010/017); `config.CurrentMode` mutable-mid-session; `session.invalid_mode` (ARCH-018).
- 059: `CompleteTurnRequest` +3 token fields → Appendix A row + ARCH-014 realtime exact-count note (supersedes the E.2b played-duration approach).
- 062: `SttUtteranceEnd` → ARCH-012 provider-contract row + server cross-doc table; `CascadeStartParams`/`StartFrameDto` `autoVad` → ARCH-009 WS-start row; `vad_events` → ARCH-012 note; **ARCH-003 auto-VAD revision note** (additive, manual preserved).

**Lessons (orchestrator banks):** the 2c mode-propagation convention (050); auto-load-early + fill-gaps + test-host-gate (055); per-turn-metric-re-anchor-must-mirror-in-aggregator + tolerant-usage-parse-with-sanitized-log (057); exact-count-realtime-pricing + cached→full-input-over-estimate-is-honest (059); **server §34** — auto-VAD-turn-end rides a real provider endpointing signal through the one idempotent `FinalizeTurn` + the WS-pump-on-its-own-linked-CTS-so-the-finally-can-cancel-it (062, extends §22).

**Future TODO (Carry-forward / G-hardening):** confirm the live cascade usage shape → bug-6 close vs the request-body opt-in follow-up (057b); the unconditional UtteranceEnd-subscribe/`vad_events` gate (062, G-hardening); per-utterance cascade turns (FE-coordinated, origin 062); the cascade-FE Stop-optional UI; the (B) turn-control-mode persistence (orchestrator's slice).

**Q4 GA field names reported (for the orch to cite in 053-C2b — landed):** `usage.input_token_details.audio_tokens` / `usage.output_token_details.audio_tokens` / `usage.input_token_details.cached_tokens` (confirmed by the live runbook fixture + Context7).
