# AI Interpreter Workbench `web/` — Build Guide

> **You're in `web/`.** This file plus root `CLAUDE.md` both load. The root file covers global project conventions + shared comm rules (track-prefix, escalation taxonomy, messaging budget); this file owns code-area conventions for the React frontend.

## Launch protocol

| Working on... | cwd | Loads |
|---|---|---|
| Planning / docs / commits | repo root (`ai-interpreter-workbench/`) | root `CLAUDE.md` only |
| the React frontend code | `web/` | this `CLAUDE.md` + root |

<!-- For a multi-area project, add a row per additional code area. -->

If you find yourself fighting the wrong conventions, check your cwd.

## Session start/end protocol

**At session start:**
1. Read `MVP_TASKS.md` (repo root) → "Currently in progress" section.
2. Confirm with the user what feature this session is targeting.
3. Read the relevant section of `ARCHITECTURE.md` from the lookup table below.

**At session end** (only when the user explicitly says we're done):

1. **Implementer runs `/session-end`.** Implementer writes ONLY:
   - `web/` code files (the slice's implementation)
   - test files (the slice's tests)
   - dependency manifest / lockfile (deps the slice adds)
   - `docs/sessions/<NNN>-<date>-<topic>.md` (session doc, created at `/session-end` Step 5)

   **Implementer must NOT touch (all orchestrator territory):**
   - `MVP_TASKS.md`
   - `web/LESSONS.md`
   - `web/CLAUDE.md` (entire file — both the Cross-doc invariants table AND the Lessons logged index)
   - `ARCHITECTURE.md`
   - `docs/orchestrator-briefing.md` / `docs/tdd-brief-template.md` / `docs/briefs/` / `docs/runbooks/`
   - other top-level deliverable / design docs
   - `.gitignore` and root-level dotfiles (unless adding a new artifact to ignore, flagged at Step 9)

   At the slice's Step 10 commit, **explicit `git add <path>` for each slice file**; **never `git add -A`** or `git add .`; **never stage an orchestrator-territory file**. If the slice surfaces a change to any orchestrator-territory file (new model needing a cross-doc table row, a lesson candidate, an architecture note), the implementer **flags it at Step 9** per the routing matrix in `docs/orchestrator-briefing.md`. The orchestrator writes the change hot during the same session — working-tree state stays aligned within the round even though commits stagger.

2. **Orchestrator runs `/orchestrate-end`** for round close-out + Carry-forward triage + round terminal commit + push.

## Lookup table — where to find canonical info

Don't paste these sections into the prompt. Grep the file:section, read only what you need. `/check-arch <topic>` dispatches off this table.

| Topic | File (relative to repo root) | Section |
|---|---|---|
| Frontend architecture (responsibilities, state shape, components) | `ARCHITECTURE.md` | §4 / ARCH-007 |
| Audio capture & format (streaming PCM + blob fallback, playback) | `ARCHITECTURE.md` | §4 / ARCH-030 |
| API contracts (sessions, turns, config, realtime token, cascade WS, evaluation) | `ARCHITECTURE.md` | §6 / ARCH-009 |
| Realtime mode (WebRTC handshake, VAD-off turns, event mapping, lifecycle) | `ARCHITECTURE.md` | §7 / ARCH-010 |
| Cascade streaming client + live transcript rendering | `ARCHITECTURE.md` | §8 / ARCH-011 |
| Metrics / latency UI (panels, tiers) | `ARCHITECTURE.md` | §10 / ARCH-013 |
| Cost panel (estimated cost/min) | `ARCHITECTURE.md` | §11 / ARCH-014 |
| WER / Evaluation panel + user flows | `ARCHITECTURE.md` | §12, §14 / ARCH-015, ARCH-017 |
| Errors / UiError / secure-context + build-run | `ARCHITECTURE.md` | §15 / ARCH-018, ARCH-019, ARCH-029 |
| Domain types mirrored from backend | `ARCHITECTURE.md` | §3, Appendix A / ARCH-005 |
| Lessons logged (full prose) | `web/LESSONS.md` | by lesson # |

<!-- Seeded from the (complete) architecture doc. Add a row whenever a new topic is looked up twice. -->

## Stack

<!-- ▼ EXAMPLE BLOCK: stack quick-reference for implementer sessions. Canonical stack lives in root CLAUDE.md + ARCHITECTURE.md; this is the cheat sheet. ▼ -->

- **Runtime:** Node 22 LTS (TypeScript 5)
- **Framework:** React 19 + Vite
- **Validation:** TypeScript types (no runtime schema lib; Zod optional later)
- **Lint / types / tests:** ESLint / tsc --noEmit / Vitest

<!-- ▲ END EXAMPLE BLOCK ▲ -->

## Standard commands

```bash
# Install deps (run once; re-run when the manifest changes)
npm install

# Run the dev server (if applicable)
npm run dev

# Tests
npm run test

# Quality
npm run lint
npm run format:check
npm run typecheck

# Preflight (use before saying "done" with a feature)
# format:check is FIRST + non-optional: a per-slice gate that omits it lets Prettier drift
# accumulate across commits, caught only at a later full gate (session 009 process finding).
npm run format:check && npm run lint && npm run typecheck && npm run test
```

## TDD protocol

**Write the failing test first.** Applies to deterministic code — see the TDD posture in root `CLAUDE.md` for what is test-first vs. exempt.

**Commit per slice when practical.** Never bundle a safety-critical slice with anything else.

## Forbidden patterns

Do not:

1. **Write code without a failing test first** for deterministic logic (state transitions, event mapping, reducers) per the root TDD posture. Browser mic / WebRTC / playback internals are exempt (manual smoke).
2. **Read or hold a standard provider API key in the frontend** — the SPA gets only the ephemeral Realtime credential (`ek_…`) from `POST /api/realtime/client-secret` (root Key safety rules; ARCH-010/019).
3. **Import transport-client internals into UI components** — components render only from `sessionStore` / `UiSessionState`; the cascade WS client + realtime WebRTC client own all wire detail (clean separation, ARCH-007).
4. **Hardcode the MediaRecorder container** — probe `MediaRecorder.isTypeSupported()` (Safari < 18.4 = mp4/AAC, not webm) and send the actual `recorder.mimeType` (ARCH-030).
5. **Show only the target transcript** — render source partials as they arrive; if Realtime input transcription is unavailable, show an explicit "source unavailable" note (PRD must-have 6; ARCH-010).
6. **`git add -A` / `git add .`** — stage slice files explicitly (root push posture).

<!-- Accretes as lessons surface; each durable rule earns a LESSONS.md entry. -->

## Cross-doc invariants — schema/docs mirroring

Several typed models in this codebase are **contracts** mirrored in `ARCHITECTURE.md` and indexed in the table below. The architecture doc is the canonical contract; the model is the executable enforcement. Drift produces silent disagreement.

**Authoring discipline (orchestrator owns this table).** When the implementer adds, removes, or renames a field on one of these models, the implementer **flags it at Step 9 categorized as `Cross-doc invariant change`** per the routing matrix in `docs/orchestrator-briefing.md`. The implementer does NOT edit `web/CLAUDE.md` or `ARCHITECTURE.md` directly — the orchestrator writes the table row + the architecture edit hot during the same session. Working-tree state aligns within the round; commits stagger (implementer's slice commit lands code+tests; orchestrator's round commit lands the doc rows).

| Model (`web/src/types/domain.ts`) | `ARCHITECTURE.md` section | Notes |
|---|---|---|
| `UiSessionState` / `TurnViewModel` / `UiError` (TS) | ARCH-007 (§4) | **View models** (frontend projections, not 1:1 wire mirrors) — verbatim the ARCH-007 §4 shapes. `UiSessionState.sessionStatus`/`turnStatus` unions = the camelCase projections of the backend `SessionStatus`/`TurnStatus` enums; the store OWNS this state machine (not on the wire). A field change here pairs with the ARCH-007 §4 TS-block edit. **D.6: `TurnViewModel` gained `latencyEvents?: LatencyEvent[]` (raw timeline w/ absolute timestamps — `deriveTurnMetrics` needs them) + `cost?: CostEstimate` (full estimate for the CostPanel tooltip); ARCH-007 §4 block updated to match.** (D.1, +D.6) |
| `InterpretationMode`/`LanguageCode`/`SessionStatus`/`TurnStatus`/`LatencyStage`/`ClockSource` (TS unions) + `LanguageDirection` | ARCH-005 / Appendix A | camelCase-as-string projections of the ARCH-005 enums (server lesson §2); `LanguageDirection = { source, target }`. (D.1) |
| `ConfigResponse` (+ `RealtimeCapability`/`CascadeCapability`/`SttCapability`/`TranslationCapability`/`TtsCapability`) | ARCH-009 / Appendix A | Wire mirror of `GET /api/config` (`Config/ConfigModels.cs`); presence-only flags (invariant #1). Consumed as `UiSessionState.providerHealth`. (D.1) |
| `CreateSessionRequest` / `InterpretationSession` / `SessionConfig` / `ProviderProfile` | ARCH-009 / ARCH-005 / Appendix A | Wire mirrors. `InterpretationSession` top-level id is **`sessionId`** (not `id`); mode/direction/models nest under `config.currentMode`/`config.direction`/`config.providerProfile.*`. `ProviderProfile` mirrored fully (10 fields, stable). (D.1) |
| `CreateTurnResponse` / `EndSessionResponse` / `CascadeTurnResponse` | ARCH-009 / Appendix A | Wire mirrors of the turn-create / session-end / cascade-blob responses. `CascadeTurnResponse.{audioBase64,audioContentType}` are response-only (never persisted, invariant #3). (D.1) |
| `TranscriptSegment` / `LatencyEvent` / `CostEstimate` / `ProviderError` (TS wire mirrors) | ARCH-005 / ARCH-012 / ARCH-013 / ARCH-014 / Appendix A | The cascade WS payload types — camelCase mirrors of the backend records (`TranscriptSegment{segmentId,role,text,isFinal,provider,timestamp,clockSource}`, `LatencyEvent{name,stage,timestamp,relativeMs,clockSource,metadata}`, `CostEstimate{…,estimatedUsdPerMinute:number\|null,…}`, `ProviderError{provider,stage,code,safeMessage,retryable,httpStatusCode?}`). Tightened out of D.1's opaque `InterpretationTurn` when the WS dispatch consumed them. The client projects `ProviderError → UiError` (drops `provider`/`httpStatusCode`, mirroring B.8). (D.4a) |
| `SessionSummary` / `ModeSummary` / `WerSummary` (TS wire mirrors) | ARCH-005 / ARCH-009 / Appendix A | Wire mirrors of `GET /api/sessions/{id}/summary` (`SessionModels.cs`) — `ModeSummary` (8 fields incl. `avgSpeechEnd*?`/`avg{Stt,Translation,TtsFirstAudio}*?`/`estimatedCostPerMinuteUsd?`, nullable doubles → `?: number\|null`), `WerSummary{sampleCount,avgWer}`, `SessionSummary{turnCount,realtime?,cascade?,wer?,computedAt,pricingConfigVersion}`. Consumed by the MetricsPanel (session averages); cascade `avgSpeechEnd*` is `n/a` (no client→server latency channel). Mirror-registration, no ARCHITECTURE.md contract change. (D.6) |
| `RealtimeTokenRequest` / `RealtimeTokenResponse` (TS wire mirrors) | ARCH-009 / ARCH-010 / Appendix A | Wire mirrors of `POST /api/realtime/client-secret` (E.1 backend `Realtime/RealtimeModels.cs`): `RealtimeTokenRequest{ sessionId, direction: LanguageDirection, model?: RealtimeModel }` → `RealtimeTokenResponse{ clientSecret (`ek_…`), expiresAt (ISO-8601), model }`. Consumed by `realtimeApi.mintClientSecret`; the `ek_` is a **transient client local** (invariant #2 — never the store / persisted / logged). **Mirror-registration** — Appendix A's `ClientSecret` DTO already carries the backend row, so **no ARCHITECTURE.md contract change**. (E.3) |
| `EvaluationPhrase` / `WerResult` / `TranscribeResponse` / `WerRequest` / `WerResponse` (+ `TranscribeParams`) (TS wire mirrors) | ARCH-009 / ARCH-015 / Appendix A | Wire mirrors of the F.1 evaluation endpoints (`Evaluation/EvaluationModels.cs` + `SessionModels.cs`): `EvaluationPhrase{phraseId,language:LanguageCode,referenceText,category}`, `WerResult{phraseId,reference,hypothesis,normalizedReference,normalizedHypothesis,substitutions,insertions,deletions,referenceWordCount,wer}`, `TranscribeResponse{hypothesis,sttProvider,sttModel,latencyEvents:LatencyEvent[]}`, `WerRequest{sessionId,turnId?,phraseId,hypothesis}`, `WerResponse{result:WerResult,persistenceWarning?:UiError}`. `TranscribeParams{sessionId,phraseId,language}` = the multipart request shape (mirrors the backend `TranscribeForm`; co-located with the other eval shapes per the `CascadeTurnParams` precedent). Consumed by `evaluationApi` + `evaluationActions` (F.2). **Mirror-registration** — Appendix A already carries the F.1 DTO rows, so **no ARCHITECTURE.md contract change**. (F.2) |
| `ComparisonTurn` (TS focused projection) | ARCH-005 / ARCH-009 / ARCH-014 / Appendix A | `ComparisonTurn = { mode: InterpretationMode; cost: CostEstimate \| null }` — a **focused read** of the wire `InterpretationTurn` from `GET /session` (NOT a full graduation of the opaque turn). **⭐ Reads the wire field `costEstimate`** (camelCase of C# `InterpretationTurn.CostEstimate`), **not** the `TurnViewModel`'s projected `cost` — `toComparisonTurn` maps `costEstimate`→`cost` (absent → `null`). Consumed by `comparisonAggregation` (F.3) for the per-model-variant cost split. **Mirror-registration** — `InterpretationTurn`/`CostEstimate` are in Appendix A; **no ARCHITECTURE.md contract change**. (F.3) |
| `SetModeRequest` / `ModeTransitionEvent` (TS) | ARCH-009 / ARCH-010 / ARCH-017 (Appendix A) | 050-frontend (Finding 2c). `SetModeRequest = { mode: InterpretationMode }` — the body of `POST /api/sessions/{id}/mode` (the TARGET mode; sent by `sessionsApi.setMode`); the frontend resyncs `config.currentMode` from the returned `InterpretationSession` (authoritative). `ModeTransitionEvent = {transitionId, fromMode, toMode, directionAtTransition, occurredAt, clockSource, triggeredByTurnId?}` — camelCase mirror of the backend `ModeTransitionEvent` record (`SessionModels.cs`), **graduated + registered for H.3 but NOT yet wired**: `InterpretationSession.modeTransitions` stays opaque `unknown[]` (the deferred-opaque row below) until H.3 consumes the timeline. **The `POST /sessions/{id}/mode` endpoint LANDED at 050-backend** (the route + `RecordModeTransition` wiring + a sanitized `session.invalid_mode` 400; `modeTransitions` stays opaque `unknown[]` until H.3 consumes the timeline). (050-frontend + 050-backend) |
| `CompleteTurnRequest` / `CompleteTurnResponse` (TS wire mirrors) | ARCH-009 / ARCH-014 / Appendix A | 053-C2b (realtime cost, frontend half). `CompleteTurnRequest = { audioDurationMs?, transcripts?, status?, outputAudioDurationMs?, inputAudioTokens?, outputAudioTokens?, cachedAudioInputTokens? }` — the body of `POST …/turns/{turnId}/complete` (the realtime finalize; cascade is WS-priced, never here); a faithful nullable wire mirror (`?: number \| null`), though the FE populates the `*AudioTokens` + `status:'completed'` + `audioDurationMs` (the recording/source-speech duration = the realtime `$/min` denominator; sent only when both recording markers present and duration > 0, else omitted — 076; mirror-registration, no ARCHITECTURE.md change). `CompleteTurnResponse = { turn: InterpretationTurn, persistenceWarning?: UiError }`. The realtime controller forwards the exact DC `response.done.usage` audio-token counts here so the backend prices from them (053-C2a). **Frontend-internal** (NOT a wire mirror — no row of its own): `RealtimeUsageTokens` (`{inputAudioTokens?, outputAudioTokens?, cachedAudioInputTokens?}` — `?: number`, absent=omitted never null) + the normalizer's `responseDone.usage` field. **Mirror-registration** — the backend `CompleteTurnRequest` + its 053-C2a token fields live in Appendix A; no separate ARCHITECTURE.md contract change here. (053-C2b) |
| `TurnControlMode` union + `UiSessionState.turnControlMode` (TS) | ARCH-007 (§4) / ARCH-005 / ARCH-003 | 063 slice 1 (Phase-I realtime auto-VAD). `TurnControlMode = 'manual' \| 'auto'`; `UiSessionState.turnControlMode` (default `'manual'`) — a **store-owned view-model field** (frontend runtime state; NOT on the wire / NOT persisted in slice 1 — the (B) session-config persistence for the G.5 write-up attribution is a separate slice). Drives the realtime controller's `session.update` `turn_detection` branch (auto → `server_vad`; manual → `null`). **REVISES ARCH-003** (the locked manual-only turn-control decision → auto-VAD additive, manual persists). A field change here pairs with the ARCH-007 §4 block + the ARCH-005 union note. (063-s1) |
| `SessionListItem` (TS wire mirror) | ARCH-009 / Appendix A | H.3-frontend (067). Wire mirror of `GET /api/sessions` (`Sessions/SessionListItem.cs`, 065): `SessionListItem = { sessionId, label?: string\|null, startedAt, endedAt?: string\|null, turnCount, modes: InterpretationMode[] }` — explicit-null, a **bare JSON array, most-recent-first** (the backend orders; the view does NOT re-sort). Consumed by `sessionsApi.listSessions` → the DI'd `historyActions.loadHistory` → `SessionHistory` (transient local display state, not `UiSessionState`); a list-fetch failure routes a sanitized `sessions.read_failed` `UiError` to the store sink. **Mirror-registration** — the backend `SessionListItem` already lives in ARCH-009/Appendix A (065), so **no ARCHITECTURE.md contract change**. (067 / H.3-frontend) |
| _Deferred-opaque (intentional, pragmatic-accrete):_ wire `InterpretationTurn` = `Record<string, unknown>`; `modeTransitions: unknown[]` | ARCH-005 / ARCH-009 | Typed opaquely until consumed — wire `InterpretationTurn` may stay opaque (the frontend builds `TurnViewModel`, not the raw wire turn). The 4 streaming wire types graduated at D.4a; `SessionSummary`/`ModeSummary`/`WerSummary` graduated at D.6 (row above). (D.1) |

> The frontend types in `web/src/types/` (`UiSessionState`, `TurnViewModel`, `UiError`, direction/mode unions) are projections of the backend contracts in `ARCHITECTURE.md` **Appendix A** / ARCH-005/007. When a slice first defines one, the orchestrator adds its row here so a shape change is paired with the matching `ARCHITECTURE.md` edit.

## Module organization

```
web/src/
  api/        sessionsApi, cascadeApi, realtimeApi, evaluationApi, configApi
  audio/      audioCaptureController, pcmWorklet, playbackController
  realtime/   realtimeWebRtcClient, realtimeEvents
  cascade/    cascadeStreamClient
  state/      sessionStore
  components/ SessionSetup, ModeToggle, RecordingControls, TranscriptPanel, MetricsPanel,
              CostPanel, EvaluationPanel, ComparisonSummary, ErrorBanner
  types/      domain, metrics
```
(Full scaffold: `ARCHITECTURE.md` ARCH-006.)

Layer dependency direction (top depends on bottom, never reverse):

```
Components (render only from store state)
  → state/sessionStore (UiSessionState)
    → api/ + transport clients (cascadeStreamClient, realtimeWebRtcClient)
      → audio/ capture + playback controllers
  → types/ (shared, importable anywhere)
```

**Components never import transport internals** — they read `sessionStore` and dispatch intents (clean separation, ARCH-007). `types/` is cross-cutting. Pin the separation with a lightweight test where practical.

## Subagents

See `.claude/agents/README.md` for the canonical inventory + integration points.

<!-- ▼ EXAMPLE BLOCK: area-specific subagent candidates — list candidates that would earn their keep specifically in this area (e.g. an ABI/types syncer for a frontend area, a Pyth/feed verifier for a contracts area). Build only on real friction. ▲ -->

## Lessons logged from prior sessions

The full prose for each lesson lives in `web/LESSONS.md`. This index is the compact orientation surface.

**Lesson numbers are stable IDs** — once assigned, they don't change. New lessons get the next sequential number. `/session-end` proposes additions when it detects them; the user approves before the entry is written and a row is added here.

Lessons start at §1.

| # | Date | Topic | Rule (one-liner) |
|--:|---|---|---|
| 1 | 2026-05-28 | [Prettier reformats area docs](LESSONS.md#1) | Add orchestrator-owned `CLAUDE.md`/`LESSONS.md` to `.prettierignore` so an implementer-run formatter can't touch them. |
| 2 | 2026-05-29 | [Minimal external store](LESSONS.md#2) | A React store = a pure `createSessionStore()` factory (unit-tested; immutable state — new ref per action, stable ref between) + a thin `useSyncExternalStore` hook (manual-smoke); the store is the single error sink; any selector variant must preserve `getSnapshot` stability. |
| 3 | 2026-05-29 | [Base-URL-agnostic clients + one `http` boundary](LESSONS.md#3) | Clients read `VITE_API_BASE_URL ?? ''` per-call (proxy + direct share code); the `http` helper is the single failure boundary — non-OK status, fetch-rejection, AND an unparseable 2xx body (`response.invalid`) all → typed `ApiError(UiError)`, never leaking a raw body/`TypeError`/`SyntaxError`; the multipart path lets `fetch` set the boundary. |
| 4 | 2026-05-29 | [Vitest fetch-mock mechanics](LESSONS.md#4) | Mint a fresh `Response` per call (`mockImplementation`, not `mockResolvedValue`) — a body is single-read; assert request args directly — `expect.anything()` does not match an absent (`undefined`) `init` arg. |
| 5 | 2026-05-29 | [Config-gating = pure selectors over `providerHealth`](LESSONS.md#5) | Realize config-gating as pure selectors (unit-tested; components render the result + dispatch intents); read model catalogs straight from `ConfigResponse` — the backend always populates them, `configured` gates the mode not the model list. |
| 6 | 2026-05-29 | [Guard a merging action's lifecycle transition](LESSONS.md#6) | When a merge/partial-update action sets a lifecycle status, guard the transition (`from===X ? Y : current`) so a merge can't drag a later state backwards — and test it from a later-lifecycle state, not just the initial one. |
| 7 | 2026-05-29 | [Side-effecting flows → DI'd actions module](LESSONS.md#7) | Extract side-effecting flows to a DI'd `*Actions` module (store + api injected); keep the component a thin caller; TDD against the real store + mocked api — pin both error arms (incl. non-typed-error fallback + no-leak), ordering, and guards, no render. |
| 8 | 2026-05-29 | [Audio capture shell + AudioWorklet gotchas](LESSONS.md#8) | Capture = manual-smoke browser shell over pure test-first helpers (conversion/probe/mic-map/duration-clamp); a capture worklet MUST `connect(destination)` (silent) or `process()` never fires, and type its realm globals via inline ambient `declare` (no `@types` dep); controller emits via callbacks (no store/transport import) + tears down on any setup failure. |
| 9 | 2026-05-29 | [Cascade WS client — fake-WS TDD + dispatch router + lifecycle hardening](LESSONS.md#9) | TDD a WS transport client against an injected fake `WebSocket` (full lifecycle); route inbound via a pure dispatch fn (audio → callback, never the store — invariant #3); harden — a server `error` is terminal, re-start tears down the prior socket, guard the dispatch BODY not just `JSON.parse`. |
| 10 | 2026-05-29 | [Normalize streaming transcripts in the store](LESSONS.md#10) | Normalize partials/finals in the store action (replace trailing partial / finalize on `isFinal` / new entry after a final; route by role) so the rendering component stays a dumb `{text,isFinal}[]` projection. |
| 11 | 2026-05-29 | [Recording orchestration + its 3 concurrency edges](LESSONS.md#11) | Wire a recording turn via a DI'd controller (createTurn→store→capture→WS, tested vs the real store + mocked deps); resolve the capture-before-socket-open races in the client (pre-open frame-queue + deferred/idempotent stop) and guard concurrent starts with an `inFlight` flag (the UI gate can't cover the async window). |
| 12 | 2026-05-29 | [Cascade playback — MSE-primary shell; blob can't progressively play live](LESSONS.md#12) | Playback = manual-smoke MSE/`HTMLAudioElement` shell over pure helpers (decode/no-overlap/once-`playback.started`/content-type clamp); MSE primary (append on `updateend`), blob fallback best-effort (a static element `src` can't progressively play a live stream); audio transient never-in-store + revoke URL on reset; wire `onAudio` via a settable delegate at the composition root. |
| 13 | 2026-05-29 | [Cascade metrics — three sources; frontend computes the deltas the backend can't](LESSONS.md#13) | Per-stage from the store (passed through, not recomputed — §7); top-level client-timing deltas frontend-computed via `Between` on absolute timestamps (the ONLY source — the backend structurally can't for cascade; never `relativeMs`; cross-clock disclosed-not-clamped); session-averages from `GET /summary`. Retain raw `latencyEvents[]` + full `cost` in the store; stamp recording.started/stopped/completed browser-clock. |
| 14 | 2026-05-29 | [jsdom/Testing-Library component tests + `errorCopy` never-raw](LESSONS.md#14) | Per-file `// @vitest-environment jsdom` (keep node default — don't disturb the node unit suite) + matcher-only `setupFiles` + per-file `cleanup`; mock browser globals via `vi.stubGlobal`; render error copy via a single pure `errorCopy(code)` map that never echoes `safeMessage` (sentinel-pinned). |
| 15 | 2026-05-30 | [Realtime WebRTC transport — shell over pure seams](LESSONS.md#15) | WebRTC transport = a manual-smoke `RTCPeerConnection`/`oai-events` shell over pure fetch-mock'd/unit-TDD'd seams (calls URL **no `?model=`**, Bearer `ek_`, `application/sdp`, **text** answer, status-derived `retryable`) + a pure stateless GA-event normalizer (`parse`+`normalize`→`NormalizedRealtimeEvent`; classify-only, E.4 stamps); the `ek_` stays a transient client local, never the store (invariant #2). |
| 16 | 2026-05-30 | [Realtime data path — per-turn stateful event sink](LESSONS.md#16) | A per-turn `createRealtimeEventSink({store,clock})` maps `NormalizedRealtimeEvent`→store: **accumulate** incremental deltas into cumulative partials (the §10 store action is cascade-cumulative — raw deltas drop tokens; reset the source accumulator on `.completed`; finalize the target on `responseDone`); first-of-type latency stamps once each (browser-clock); **`audioDelta` is timing-only** (never store audio — #3, enforced by an audio-sink-less `Pick<SessionStore>`); the store owns `turn.completed`; report client events via `appendTurnEvents` so the backend aggregates the canonical realtime metrics (§13 realtime half). |
| 17 | 2026-05-30 | [Realtime turn control — DI'd controller, buffer-delimited turns](LESSONS.md#17) | `createRealtimeTurnController({store,client,api,clock})` (the §11 analogue) sequences a manual VAD-off turn: createTurn→beginTurn→lazy-connect→per-turn sink wired to the client's raw `onServerEvent`→**buffer-delimited** turns (`session.update turn_detection:null`+`input_audio_buffer.clear` on Start; `commit`+`response.create` on Stop — mic streams continuously, no per-turn toggle)→browser-clock `recording.started/stopped`→report on `responseDone`; dispatched from RecordingControls by `currentMode` (the real entry point); `normalize` moved INTO the tested controller (shrinks the exempt shell); interim audio = a detached `<audio>` (live track, never stored — #3) with a once-stamped `playback.started`. |
| 18 | 2026-05-30 | [Realtime connection lifecycle — DI'd manager, persistent pc](LESSONS.md#18) | `realtimeConnectionManager` owns ONE pc held across turns (idempotent `ensureConnected` — discharges the double-connect guard; **the latch resets on a failed connect** so a later turn retries + the controller fails/aborts that turn — else a transient fail bricks realtime); `realtime.session.connecting` stamped at connect-**initiation** (→ `realtime_connect_ms`), `connected`/`disconnected` from the pc connectionstate (a settable `onConnectionState` shell delegate → tested mapper); a disconnect is **surfaced** (sanitized `realtime.session.disconnected` UiError → failTurn[active, populates turn errors]/addError[between] + errorCopy advise-switch — never swallowed, ARCH-018); `teardown()` (close/stop/release/detach-`<audio>`/reset-latches) on End, wired into SessionSetup. |
| 19 | 2026-05-30 | [Standalone EvaluationPanel — persist WER via a dedicated eval turn](LESSONS.md#19) | WER persists only when `POST /wer` carries a `turnId` (F.1a attaches via `UpdateTurn`); `transcribe` is stateless. A standalone panel persists by creating a **dedicated eval turn**: `record→transcribe→createTurn→computeWer({turnId})` (createTurn AFTER a successful transcribe so a failed transcribe doesn't orphan a turn; createTurn-failure is its own abort arm). The eval turn is a **backend-only artifact** (the `WerResult` attach point) — the store's interpretation-turn machine (`currentTurn`/`turns[]`) stays UNTOUCHED. It inherits the session mode → counts toward `ModeSummary.TurnCount` (documented limitation; quality averages null-skip + unaffected, `WerSummary` exact; a backend exclusion-marker is the clean fix, deferred). A `computeWer` failure after `createTurn` orphans the turn (bounded; track a cleanup endpoint). |
| 20 | 2026-05-30 | [One-shot eval capture — reuse `recordBlob()`; DI'd flow returns transient result, errors→store](LESSONS.md#20) | Reuse `audioCaptureController.recordBlob(durationMs)` for the one-shot eval recording (no 2nd MediaRecorder; named `EVAL_RECORD_DURATION_MS`); **`recordBlob` returns `null` SILENTLY on mic-fail** (unlike `startStreaming`'s `onError`) → the flow surfaces its own `capture.failed`. POST multipart like `cascadeApi` (no content-type header). The DI'd `evaluationActions` flow **RETURNS** `{hypothesis, werResult}` for LOCAL panel display (transient display state, not `UiSessionState`); errors + a `WerResponse.persistenceWarning` route to the store (single sink, §2/§7; warning mirrors `endSession`). Panel = thin render+dispatch (ARCH-007) with a `useRef` inFlight guard (§11 — the disabled button can't cover the async double-click window) + an active-session gate (reuse the existing `canStartRecording` selector — import, don't re-author) + fetch-phrases-on-mount. |
| 21 | 2026-05-31 | [Cost-by-model-variant comparison — pure frontend aggregation over wire `costEstimate.model`; 2 sources no double-truth; `costEstimate`≠`cost`](LESSONS.md#21) | `ModeSummary` is per-mode only → the **per-variant cost split is a pure frontend aggregation** over per-turn cost (§13). **Two sources, each authoritative — no double-truth:** per-mode aggregates + `WerSummary` from `GET /summary`; the per-variant split from `GET /session`'s persisted turns (canonical, both modes — realtime cost is `/complete`-priced + may be absent from the live `UiSessionState.turns`). **⭐ The wire turn's cost field is `costEstimate`, NOT the viewmodel `cost`** (raw `GET /session` = camelCase of C# `InterpretationTurn.CostEstimate`; reading `cost` silently empties the breakdown) — read via a focused `ComparisonTurn = {mode, cost: CostEstimate\|null}` projection (keeps the opaque turn opaque), pin with a direct projection test. Degrade three-valued: `byVariant null` = source-failed (independent of the per-mode summary) / `[]` = no priced turns / rows; skip null **AND non-finite** cost (`Number.isFinite` — `NaN`/`Infinity` poisons the avg), never a synthetic 0; "n/a" for missing fields; WER unclamped (`×100`, >100% valid). |
| 22 | 2026-05-31 | [Styling slice — vendor design tokens; map kit-mock→real state](LESSONS.md#22) | A styling slice is CSS/`className`/markup-for-styling only over store-driven components (clean-separation held; manual-smoke-verified, not RED-first); vendor the design's tokens (`web/src/styles/tokens.css`) + adapt its stylesheet (`workbench.css`) — plain CSS, no Modules/Tailwind — imported in `main.tsx`. A delivered design KIT's **mock store is a visual reference ONLY** — map to the REAL `UiSessionState`/selectors (`modeAvailability`/`availableModels`/`deriveTurnMetrics`/`loadComparison`), keep our behaviors, **never copy mock fields or import mock interactions** (copying a mock field silently empties the UI). Tests query role/`aria-label`/text/`data-final` not className → wrappers safe; preserve every queried hook + exact button names (`aria-hidden` decorative taglines); **adjust a query, never an assertion** — and not even a query if it would change *what* is asserted (style mode cards in blue=Realtime/violet=Cascade + apply the table treatment only where already tabular). Unit-test the one deterministic display helper (latency-vs-target, ARCH-013 thresholds; `errorCopy` precedent); manual-smoke the rest. |
| 23 | 2026-05-31 | [Realtime latency stamps are per-turn in the sink, not a session-`<audio>` once-stamp](LESSONS.md#23) | Per-turn latency stamps for a **session-persistent** transport (the realtime pc / its `<audio>`, §18) MUST be scoped per-turn — stamp in the per-turn event sink (§16); a session-level once-latch leaks a prior turn's stamp onto a later turn → **negative** `speechEndToPlaybackMs` (a stamp LEAK within one clock — distinct from §13 cross-clock skew, which is disclosed-NOT-clamped; don't blanket-clamp negatives). Stamp realtime `playback.started`/first-audio per-turn on the first post-stop `audioDelta`; DROP the session-`<audio>` `onplaying` stamp (it still plays). Also implement the metric's FULL documented selection chain (ARCH-013 `tts.first_audio ?? realtime.first_audio_delta ?? playback.started`) — reading only the first source left the realtime headline a permanent n/a (doc-vs-code drift). Refines §16/§17. |
| 24 | 2026-05-31 | [Status-gated action: not-live DENYLIST gate (not active-only allowlist) + self-clear-before-retry inside the action + normalize failure to one actionable code](LESSONS.md#24) | A status-gated mutating action (e.g. `switchMode`) must guard **all** non-live statuses as a **denylist** (`sessionId===null \|\| sessionStatus 'ended'/'ending'` → store-only; POST only when LIVE — `active`/`readyForTurn`). An active-only allowlist (`=== 'active'`) silently SKIPS the POST for a live `readyForTurn` session → re-diverges 2c the moment something sets it; the denylist's worst case (a future not-live status POSTs-and-404s) self-recovers, an allowlist's miss is silent data corruption. **Self-clear the prior error INSIDE the action** at the start of a real attempt (`store.clearErrors()` like `startSession` — robust to any callsite, NOT the `ModeToggle` onClick; `canToggleMode` doesn't gate on errors, so otherwise the toggle stays enabled-but-wedged → "must refresh"). **Normalize ANY failure to ONE sanitized `session.mode_switch_failed`** via a bare `catch {}` (keep prior mode) → no raw `http.404`/backend code reaches the banner (invariant #4 strengthened) + `errorCopy` maps one code to actionable copy, never the generic fallback. Compound `||` early-return preserves `sessionId`→string narrowing. |

| 25 | 2026-05-31 | [Per-stage metrics are DURATIONS (difference absolute-timestamp markers, keyed to the panel); anchor a pre-VAD speech-end on `stt.final`; badge responsiveness not total-turn](LESSONS.md#25) | A live smoke found per-turn cascade metrics wrong on a COMPLETED turn. **⭐ Per-stage values are DURATIONS** — `deriveTurnMetrics` differences stage markers via absolute-timestamp `Between` (`stt.final`−`cascade.audio.received`, `translation.final`−`translation.started`, `tts.complete`−`tts.started`), keyed `stt`/`translation`/`tts` to the panel; it previously forwarded the raw `{eventName: relativeMs}` map (keys never matched → **permanent `n/a`** on an all-events-present turn). Omit absent/negative (honest `n/a`, never a fabricated 0). **⭐ Pre-VAD, anchor `speech_end_to_first_audio_ms` on `stt.final`** (Deepgram endpointing ≈ true speech-end), NOT the manual `turn.recording.stopped` (held seconds past speech → negative headline); fall back to `recording.stopped` when absent; realtime unchanged. Keep the literal-`recording.stopped` `speech_end_to_playback_ms` distinct + backend-consistent (Phase-I VAD fully resolves it). **⭐ Badge responsiveness, never total-turn** (`<3s`/`<1.5s` are responsiveness targets; total-turn includes talk+hold → a red badge on a responsive turn); total-turn = secondary no-badge row; `latencyTier` → `na` for a negative value (disclosed, badge muted, value not clamped — §23). Cost `n/a` + the coincident `tts.started`==`tts.first_audio` 0 ms are **backend** follow-ups, not frontend patches. |
| 26 | 2026-05-31 | [Realtime cost finalizes at `/complete` with DC `response.done.usage` exact token counts — extract in the normalizer, dual-read the nesting, real-0≠absent, never synthetic $0](LESSONS.md#26) | The realtime FE never POSTed `/complete` (only `/events`) → the backend's exact-count pricing (053-C2a) never ran → cost `n/a`. Wire `/complete` on `response.done`: extract `usage` in the **pure normalizer** (`responseDone.usage`; the controller stays thin + POSTs as an independent sibling to `/events`), leave the sink store-only (invariant #3). **Dual-read the usage nesting** (`response.usage ?? top-level usage`) + pin BOTH shapes — a wrong single path silently reproduces the `n/a` under green tests (§15/§18 verify-the-live-GA-shape). **Distinguish a real cached=0 from absent** (never fabricate 0); absent/malformed usage → null but STILL POST to finalize → backend discloses-unavailable, never a synthetic $0 (§23 / server §25). Surface a `/complete` failure sanitized (`realtime.complete_failed`). |
| 27 | 2026-05-31 | [Auto-VAD realtime (Phase I) — `turn_detection: server_vad` in session.update, auto-Stop no-op, single-utterance settled latch; split so the event-sequence-independent half ships first](LESSONS.md#27) | Phase I auto-VAD realtime, slice 1 (toggle+config; REVISES ARCH-003). The realtime VAD mode is **FE-controlled** via `session.update` (the mint just defaults it) → no backend change. `turnControlMode: 'manual'(default)\|'auto'` drives the branch: auto sends `turn_detection:{type:'server_vad', …GA-default constants}` + re-asserts `transcription` (053-B); **auto-Stop is a NO-OP** (server owns segmentation; a manual commit RACES the auto-commit → double response — defer-to-the-evidence over the brief's early-end vote). **Bound a config-only slice to single-utterance via a per-turn `settled` latch** (idle after the first auto `response.done`, reset on next Start; a 2nd utterance can't re-finalize/garble; a DEV warn flags the drop). **Split a verification-dependent feature** — slice 1 is event-sequence-INDEPENDENT (reuses the verified `response.done` path) → ships WITHOUT a live server-VAD capture; the multi-segment lifecycle + sink change + buffer-event-sequence verification = slice 2 (§15/§26 verify-the-live-GA-shape). Extends §17. |
| 28 | 2026-05-31 | [Realtime auto-VAD must NOT gate its lifecycle on the unconfirmed `speech_started` — begin on the confirmed `committed`/`response.created` too (guarded → one begin)](LESSONS.md#28) | 070 / Phase-I Bug C. The live smoke found realtime never auto-finalized: the I.2-s2 lifecycle (`06a604a`) gated turn-begin on `input_audio_buffer.speech_started` — NEVER live-confirmed (053-C was MANUAL/`turn_detection:null`) → if it doesn't fire, `beginAutoSegment` never runs → `response.done` finalize gated out → turn hangs. **Fix:** begin on the 053-C-CONFIRMED `committed`/`response.created` too (+ `speech_started`), guarded (`segmentStarting`/`currentSegmentTurnId`) → EXACTLY one begin → finalizes via the same idempotent seam regardless. Preserve the 3A speech-end anchor in the fallback. Browser-exempt live-confirm (ARCH-020); reviewer-deferred: stale-anchor-between-segments + the `responseCreated`-only fast-`response.done` race. Extends §15/§26 to a lifecycle gate. |
| 29 | 2026-05-31 | [Never send a realtime client event before the RTCDataChannel is `open` — queue-until-open + flush-on-`onopen` (a no-op DROPS the config)](LESSONS.md#29) | 072 / Phase-I P0 — realtime 100% DEAD: `sendClientEvent` was `dataChannel?.send(...)` (`?.` guards null NOT `readyState`) → `session.update` on a `connecting` DC threw `InvalidStateError` → `startTurn` rejected → turn never inits (both modes; latent I.2-s2 ordering, orthogonal to 070). **Fix = a pure `createClientEventQueue({isOpen,rawSend})`** (buffer-while-not-open, flush-IN-ORDER on `onopen`, clear on teardown) wired into `sendClientEvent`. **⭐ A no-op-when-not-open is a TRAP** — it'd DROP the `session.update` (server-VAD never configured → still broken); the queue sends it AFTER open. Pure queue unit-tested (+ the fake-DC `onopen→flush` wiring pin); DC plumbing manual-smoke (lead's re-test). Reviewer-deferred LOW: `MAX_PENDING` cap, flush-throw guard, null DC handlers in teardown. |
| 30 | 2026-05-31 | [GA Realtime `session.update` requires `session.type:"realtime"` on the session object — omit → reject → no config → silent "ready"-hang](LESSONS.md#30) | 073 / Phase-I P0 — the ROOT CAUSE of the whole realtime saga (bug C→070→072→073), **LIVE-confirmed fixed**. Realtime was 100% dead: OpenAI rejected EVERY `session.update` (`missing_required_parameter: session.type`) → no config → `turn_detection` null → no auto-VAD/transcription → "ready"-hang (no crash; masqueraded as backend). **Fix:** add `type:"realtime"` to the `session` object of every `session.update` (one-line, single `sessionUpdateInput` seam — manual/auto/auto-stop). Context7: placement on the `session` object (the `session.created` echo confirms); incremental patch preserves the minted config (transcription re-assert rides); no 2nd masked field. KEEP 072 DC-gate + 070 decouple. **Meta:** the §18/§20-deferred hedge (070) was right to defer (don't speculatively change a confirmed payload) AND right to add once the live error named the field. |
| 31 | 2026-05-31 | [A fair cross-mode `$/min` needs the SAME denominator basis both sides — supply the realtime denominator from recording duration, omit-never-0](LESSONS.md#31) | 076 / realtime $/min. Realtime `estimatedUsdPerMinute` was null (blanking the comparison cost axis) because the FE `/complete` never sent `audioDurationMs`; the backend `Build` already divides by it. `finalizeTurn` now sends the recording (source-speech) duration (`recording.stopped−recording.started`) → `$/min` on the SAME source-speech-minute basis as cascade (apples-to-apples). The input+output basis was REJECTED (~2× skew). Omit when a marker is absent or duration ≤0 (honest-degrade, never a synthesized 0). Disclosed in CostPanel + ComparisonSummary. No backend/wire-shape change. |

<!-- Starts empty. Each row links to its `LESSONS.md` anchor. -->

<!-- Slash commands: see root CLAUDE.md "Slash commands available." Implementer pair: /session-start + /session-end. -->
