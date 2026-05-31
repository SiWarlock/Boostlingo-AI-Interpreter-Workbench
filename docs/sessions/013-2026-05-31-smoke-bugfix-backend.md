# Session 013 — Real-key-smoke backend bug-fix cycle (G.4)

- **Date:** 2026-05-31
- **Phase:** G.4 (stability hardening — real-key-smoke-surfaced bugs)
- **Role:** backend implementer (`boostlingo-main-backend-implementer`)
- **Predecessor:** [012 — Phase-F F.4 eval-turn exclusion](012-2026-05-30-phase-f-f4-eval-turn-exclusion.md)
- **Successor:** [016 — Backend: mode-switch, .env loader, cost arc, Phase-I cascade auto-VAD](016-2026-05-31-backend-cost-arc-and-phase-i-cascade-autovad.md)

## Why this session existed

The user ran the first **real-key smoke** (live Deepgram + OpenAI) of the cascade path. It surfaced live-integration bugs the fake-provider suite (ARCH-020 real-network-exempt) structurally couldn't catch — cascade translation 400'ing on every turn, an alarming `[Error] Stop: NullReferenceException` in the backend log, and a `cascade.empty_transcript` failure on a fully-correct turn. This session fixed the cascade blockers across three slices. (A parallel `web/` impl handled the frontend halves — commits `1b352e0`/`7dc398e`/`27cab1f`/`ccfcc17`/`e777a31`, not in this doc.)

## What was built

### Slice 048 — cascade WS stop-path ARCH-018 hardening + idempotent Deepgram Stop (`ccd9cb6`)

**Finding-first reversal:** the briefed PRIMARY bug ("unhandled `NullReferenceException` in our cascade WS Stop/finalize path") **did not exist as described**. Evidence (escalated as a Finding before any code): `log.txt` is entirely **Deepgram SDK v6.6.1 internal logging** — every line is an SDK method-name category (`DeepgramWsClientOptions:`/`Connect:`/`Stop:`/`ProcessReceiveQueue:`), and `[Error] Stop: <ex> thrown <msg>` is the SDK's own catch-and-log format. The NRE is triggered by our **double-`Stop()`** call and is **caught + logged + swallowed by the SDK** — it never escapes `DeepgramSttProvider` (both `Stop()` call sites were already exception-guarded). Reframed (orchestrator-approved) to a small manual-smoke degrade-don't-crash hardening.

**Files modified:**
- `server/AiInterpreter.Api/Cascade/CascadeWebSocketEndpoint.cs` — broadened `HandleAsync`'s OCE-only catch to also catch any `Exception` → log server-side single-lined + emit ONE sanitized terminal `error` frame (`ProviderErrorMapper.Unknown("cascade","cascade")` → `cascade.unknown`) + best-effort close (ARCH-018 / invariant #4). Added an `ILogger<CascadeWebSocketEndpoint>` ctor param + a `TrySendErrorFrameAsync` helper. Consolidated `RunTurnAsync`'s channel-writer-complete + pump-await into one `try/finally` so the audio pump can't park/leak on any exit path (Done / Done-less / exception).
- `server/AiInterpreter.Api/Providers/Deepgram/DeepgramSttProvider.cs` — routed both `client.Stop()` call sites (pump graceful-close + iterator-finally teardown) through one `Interlocked`-guarded `StopOnceAsync` so the SDK `Stop()` runs at most once (kills the misleading double-Stop `[Error]` log noise).
- `server/AiInterpreter.Api/Program.cs` — pass `ILogger<CascadeWebSocketEndpoint>` into the `AddScoped` factory.

### Slice 051 — real translation model names + rates (`8c46d13`) — THE headline blocker

The configured translation model `gpt-5.4-nano`/`gpt-5.4-mini` is **not available on the user's OpenAI key** (real but gated/newer → HTTP 400), so cascade translation 400'd every turn (the user's "I keep getting failures"). Switched to the broadly-available `gpt-5-nano`/`gpt-5-mini` (user-chosen) with real published rates. **Translation only** — realtime/TTS/transcribe/Deepgram STT confirmed available, untouched.

**Files modified:**
- `Config/ConfigService.cs` — translation catalog → `["gpt-5-nano", "gpt-5-mini"]`.
- `Providers/OpenAI/OpenAiOptions.cs` — default `Model` → `"gpt-5-nano"`.
- `config/pricing.json` — real per-1M rates (`gpt-5-nano` 0.05/0.40, `gpt-5-mini` 0.25/2.00; no 0.0 placeholder); `version` → `"2026-05-31-payg-estimates"`. Cached-input omitted (the `TranslationModelRates` schema has no such field).
- `.env.example` — `OPENAI_TRANSLATION_MODEL` default + comment.
- `server/AiInterpreter.Tests/*` (20 files) — name swap + rate/version assertion updates; a new RED-first **guard test** (`CostEstimatorTests.every_configured_translation_model_prices_to_a_nonzero_estimate`) couples the ConfigService catalog ↔ pricing ↔ non-zero so a not-on-key/unpriced/placeholder model can't ship silently again; rewrote the lesson-§9 zero-rate pin onto a synthetic config; deleted the obsolete `gpt_5_4_mini_placeholder_present` (it pinned the bug).

### Slice 052 — tolerate spurious empty STT finals (`79ef9c7`) — the active blocker after 051

The orchestrator failed the WHOLE turn on the FIRST empty/whitespace STT final. Deepgram emits empty finals around real speech (leading silence, VAD boundary, or a **trailing** empty after the content). The live case: a fully-correct turn (transcribed → translated → played, all stage events fired) was marked failed by a trailing empty final at ~7367ms. The inverse of the §28 join-finals lesson.

**Files modified:**
- `server/AiInterpreter.Api/Cascade/CascadeStreamingOrchestrator.cs` — `SttFinal` case SKIPs an empty/whitespace final (no `stt.final` stamp / source seg / translate / fail; continue). Fail `cascade.empty_transcript` only at stream end when `sawEmptyFinal && !sawNonEmptyFinal`. Pure silence (no final) still Completes (Q4).
- `server/AiInterpreter.Tests/CascadeOrchestratorTests.cs` — scripted-finals fake + 4 RED-first tests (trailing-empty-after-real / leading-empty-then-real / all-empty-fails / single regression) + a comment refresh on the existing single-empty test.

## Decisions made

- **048 reframe (Finding):** the `[Error] Stop:` NRE is Deepgram-SDK-internal caught noise, not our crash — verified the log CATEGORY against the SDK's method names + our exception-guarded `Stop()` call sites. Reframed to a manual-smoke ARCH-018 hardening (broadened catch + idempotent Stop + pump-cleanup `try/finally`). Manual-smoke (WS shell + provider transport are ARCH-020-exempt) — no unit tests; the lead's live cascade re-test validates the happy path.
- **052 two-flag refinement (corrected the brief):** the brief's single `sawNonEmptyFinal` flag would have regressed pure silence (must stay Completed). Used **two flags** (`sawEmptyFinal && !sawNonEmptyFinal`) to distinguish pure-silence (Completed) / all-empty (empty_transcript) / has-content (Completed). Bonus: skipping the empty final's `stt.final` STAMP fixes the inflated-latency symptom (the late empty final no longer drives the metric).
- **051 guard couples catalog↔pricing↔non-zero** (not just "default model priced") so a 0.0-placeholder or off-catalog model is caught; omitted cached-input (schema has no field — don't invent schema).
- **`ProcessSendQueue: NullReferenceException` confirmed benign** (052 secondary): same SDK-internal teardown noise — fires immediately after `Stop:` (L216→217, L237→238, L258→259), AFTER audio sent + transcription complete, not mid-send; the live retry transcribed fully + correctly → no frames dropped. No code change.

## Decisions explicitly NOT made (deferred)

- **050-backend (`POST /api/sessions/{id}/mode`)** — NOT built this session (wholesale team cycle; 052 was the last slice). The design is **pre-approved + fully captured in brief 050**: an atomic re-entrant `SwitchMode` store method (records the `ModeTransitionEvent` via the built-but-unwired `RecordModeTransition` + swaps `CurrentMode` under one gate), `SetModeRequest` in `SessionDtos.cs`, controller `Enum.IsDefined` → `400 session.invalid_mode` (add to `ErrorSanitizer.SafeMessageForCode`) / null → 404, the 2c e2e pin. **The fresh backend impl picks it up straight-to-GREEN and must re-verify the shipped frontend `SetModeRequest` field casing (commit `7dc398e`) against its DTO.**
- **`ProcessSendQueue` NRE** — not fixed (benign SDK noise). If a future smoke shows it dropping frames, it becomes a finding.
- **048 cosmetic LOW** (`Unknown("cascade","cascade")` provider==stage) — left as-is (consistent with the `EmptyTranscript` factory precedent; code is `cascade.unknown`).

## TDD compliance

**Clean.**
- 048 — manual-smoke (WS shell + provider transport ARCH-020-exempt; orchestrator-approved); the pure `CascadeWsMapping` is unit-tested, the shell is not. No deterministic logic shipped untested.
- 051 — RED-first guard test (confirmed RED: `'gpt-5.4-mini' prices to 0`) → GREEN; name-asserting tests updated.
- 052 — RED-first (trailing + leading empty-final tests confirmed RED: Expected Completed, Actual Failed) → GREEN.

## Reachability

- 048 — the broadened catch + `try/finally` + idempotent Stop are reachable from the production WS route `app.Map("/api/cascade/stream", …)` → `CascadeWebSocketEndpoint.HandleAsync` (the live cascade turn entry point).
- 051 — the catalog/default/rates are reachable via `GET /api/config`, the cascade translation path, and `CostEstimator` (live-confirmed: the user's dropdown shows `gpt-5-nano`, no more 400).
- 052 — `CascadeStreamingOrchestrator.RunAsync` is reachable from the WS endpoint + the blob fallback.
- **Known gap (not this session):** the shipped frontend `sessionsApi.setMode` POSTs `/api/sessions/{id}/mode`, which **does not exist yet → 404**. The endpoint is 050-backend (pre-approved, brief 050) for the fresh impl. Tracked, not a silent gap.

## Open follow-ups (Step-9 categorized — owed by the FRESH orchestrator at round-seal)

The current orchestrator handed off the round-seal (past its 75% ACTION threshold). All implementer slice commits are landed + HEAD-verified; the doc-routing below is in `MVP_TASKS.md`'s round-seal checklist for the fresh orchestrator:

- **server lessons (LESSONS.md + index):** (a) a WS/stream stop-finalize path must catch broadly + degrade to a sanitized terminal; a callback-SDK client whose `Stop()`/dispose isn't double-call-safe needs an idempotent stop guard — **and an SDK's own caught-and-logged internal exception can masquerade as "our" crash; verify the log CATEGORY before trusting a brief's diagnosed locus** (the 048 meta-insight). (b) model NAMES are external reality — pin the configured default to a real, priced model with a guard test (051). (c) a streaming STT consumer must tolerate spurious empty/whitespace finals (skip; fail only if none non-empty ever arrives) — the inverse of §28 (052).
- **ARCHITECTURE.md realization notes:** ARCH-011/018 — the cascade WS stop path fails closed (sanitized `cascade.unknown`) on any unexpected exception (048); ARCH-011 — empty STT finals are skipped; fail `empty_transcript` only when no non-empty final ever arrives (052).
- **Cross-doc invariant change (051):** the `TranslationModel` model-union literals `gpt-5.4-*` → `gpt-5-*` → ARCH-005 + Appendix A + `server/CLAUDE.md` + `web/CLAUDE.md` rows; pricing keys + rates → ARCH-014 + the version note (`2026-05-28` → `2026-05-31-payg-estimates`); `.env` default → ARCH-028; + a README clean-clone gotcha (configured models must be available on YOUR OpenAI key). Closes the Decisions-tabled "re-verify pricing.json model names + rates at build." `ErrorSanitizer.SafeMessageForCode` did NOT gain `session.invalid_mode` (050-backend not built).
- **Carry-forward (orchestrator triage):** 050-backend (the mode-switch endpoint, brief 050; frontend 404'ing against it); the per-stage-metrics-show-`n/a`-on-a-failed-turn symptom is FRONTEND (MetricsPanel) — expected to resolve once a turn SUCCEEDS post-052; if it persists on a succeeded turn, a small frontend guard fix.

## How to use what was built

Nothing operator-facing changed. The cascade now: (1) translates with a real, on-key model (051), (2) tolerates spurious empty STT finals instead of failing correct turns (052), (3) degrades any unexpected WS-turn exception to a sanitized terminal instead of an unhandled crash + no longer logs the misleading double-Stop NRE (048). The lead restarts `:5179` after each backend commit for the user's live re-test.
