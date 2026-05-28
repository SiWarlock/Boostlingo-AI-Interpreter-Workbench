# MVP_TASKS.md — AI Interpreter Workbench

> **Phase note.** This is the single source of truth for build state + the phase plan. It is generated from `ARCHITECTURE.md` (the design contract) and must not invent architecture not present there — if a task needs something the architecture lacks, flag it and add it to `ARCHITECTURE.md` first. Locked decisions (ARCH-003): TS SPA + .NET/C#; Realtime via browser WebRTC + backend-minted ephemeral credential; Cascade as a **fully streaming** Deepgram `nova-3`(`multi`) → OpenAI `gpt-5.4-nano`/`mini` → OpenAI `gpt-4o-mini-tts` pipeline; turn-based click start/stop (delimits a turn; audio streams within it); local JSON persistence (no raw audio, no secrets); config-driven estimated cost/min; scripted WER. **Build order is local-first; backend seams + tests precede UI; the two highest-risk integrations (Realtime WebRTC = Phase E, live-streaming cascade = Phase C/D) are sequenced last, each with a documented fallback.** The `.claude/` agent-team scaffolding is now in place (root `CLAUDE.md`, `server/` + `web/` area guides + `LESSONS.md`, `.claude/commands` + `.claude/agents`, `docs/` briefing + team-protocol + brief template). The Session protocol block below is active.

> **Session protocol:**
> - **Session start** — orchestrator runs `/orchestrate-start`; implementer runs `/session-start`. Confirm the session target.
> - **Session end** (only when the user says we're done): implementer `/session-end` (TDD + cross-doc audit + `/preflight`); orchestrator `/orchestrate-end` (reconcile checkboxes, append Log, round commit + push).

> **Reference deadlines / budget:**
> - Build ceiling: **~5 days (≈ 2026-06-02)**; aim to finish faster and keep scope tight. Favor the architecturally-correct, PRD-faithful build; do not gold-plate — use the Trims catalog and documented fallbacks as the pressure valve.

> **Spec-anchor convention (architecture-as-contract).** Each phase header carries a `**Spec anchors:**` block listing the `ARCH-###` sections it implements; each task also carries an authoritative inline `Anchors:` line (the header block is a redundant summary). Re-read the listed anchors at session start. If a slice surfaces behavior the anchors don't cover, that's a cross-doc invariant flag — either the anchor is missing or the implementation has drifted. **Cite `ARCH-###` (stable IDs), not `§` numbers.** **Path convention:** backend file paths are relative to `server/AiInterpreter.Api/`, test paths to `server/AiInterpreter.Tests/`, and `web/...` paths are repo-root-relative (per ARCH-006). **Intentionally uncited meta anchors:** ARCH-001 (exec summary), ARCH-024 (alternatives), ARCH-026 (sequencing — realized by the phase plan).

---

## Currently in progress

**A.5 — Host, CORS, health** (backend; `Program.cs` DI + flat-env→section bridge + `JsonDefaults` HTTP pipeline + pricing loader + CORS + WebSockets + port 5179 + `GET /api/health`; first integration tests). Brief: `docs/briefs/005-A.5-host-cors-health.md`. _(A.5's web Vite dev-config re-sequenced to D.1 — see brief. Closes Phase A.)_

**Last landed:** `A.4` pricing config + loader (code `4ed26a6` + docs `d19cc01`); `A.3` domain (`3d0f7d7` + `63df8f0`); `A.2` config/Options (`4e4d11f` + `fda80f0`); `A.1` scaffold (`ef05ccb`) + foundation docs (`38e57a4`..`fac93af`). **Commit cadence:** commit-as-we-go on `main` per logical unit (push deferred to `/orchestrate-end`; no remote).

**Next after A.5:** Phase B — backend core seams + tests against fakes (B.1 provider interfaces → B.10 boundary tests).

---

## Carry-forward to upcoming briefs

_Entries carry an origin marker `(origin: YYYY-MM-DD <slice-id>)`. Items that belong to a specific phase are inlined as task checkboxes there (not held here); only next-1–2-slice items live here._

_(Empty at Phase-A close — all items closed or inlined to their phase. **Closed:** WAE-scope (decided A.5 — Api-only WAE, no `Directory.Build.props`); A.1 template reconciliation (done A.5 — port 5179, template profiles dropped, Swagger Dev-only). **Inlined to B.9:** collection-size bounding + `Result`→DTO mapping (origin A.3) + global sanitizing exception handler (origin A.5, safety). **Inlined to C.4:** cascade WS `Origin` validation (origin A.5).)_

---

## Deliverable map

| Deliverable (PRD / ARCH) | Status | Delivered by |
|---|---|---|
| Browser SPA + mic capture + playback (PRD 1; ARCH-007, ARCH-030) | ❌ | Phase D |
| Realtime mode — voice in/out via `gpt-realtime` (PRD 2; ARCH-010) | ❌ | Phase E |
| Cascade mode — streaming STT→Translation→TTS (PRD 3; ARCH-011) | ❌ | Phases B, C, D |
| Mode toggle, mid-session or pre-session (PRD 4; ARCH-007, ARCH-017) | ❌ | Phases D, E |
| Language pair EN↔ES (PRD 5; ARCH-002) | ❌ | Phases A, D |
| Live source + target transcripts as produced (PRD 6; ARCH-007, ARCH-011) | ❌ | Phases D, E |
| Per-stage latency visible (PRD 7; ARCH-013) | ❌ | Phases B, D |
| Provider abstractions + fakes (PRD; ARCH-012) | ❌ | Phase B |
| Streaming throughout, no full-utterance blocking (PRD; ARCH-011) | ❌ | Phases C, D |
| Error handling: rate limits/timeouts/empty/mic-denied (PRD; ARCH-018) | ❌ | Phases B, C, D |
| Targeted tests on cascade + provider boundaries (PRD; ARCH-020) | ❌ | Phases B, C |
| Local JSON persistence, no raw audio/secrets (ARCH-016, ARCH-019) | ❌ | Phase B |
| Cost estimate/min (PRD impact metric; ARCH-014) | ❌ | Phases B, D, F |
| WER Evaluation panel (committed MUST; ARCH-015) | ❌ | Phase F |
| Comparison summary view (ARCH-009 summary; ARCH-007) | ❌ | Phase F |
| README + CLAUDE.md/AGENTS.md (PRD; ARCH-023) | ❌ | Phase G |
| Comparison write-up 1–2 pp (PRD 8; ARCH-023) | ❌ | Phase G |
| 5-min stability: no disconnect/drift/leak (PRD; ARCH-020) | ❌ | Phase G |
| Meaningful scoped git history (PRD; ARCH-023) | 🟡 | All phases |

---

## Phase exit checklist (applies to every phase)

- [ ] All phase task checkboxes ticked (partial work stays unchecked with a Log note).
- [ ] Acceptance criterion met (`/preflight` clean + manual smoke where there's runtime behavior).
- [ ] All phase tests green; no model field changed without the matching `ARCHITECTURE.md` edit in the same commit round (Appendix A invariants).
- [ ] Session doc(s) list every file created/modified.
- [ ] Commits scoped + pushed.

---

## Final-submission acceptance criteria (project-level)

The project is "done" (ARCH-025 + PRD success criteria) when:

- [ ] Local app runs from a clean clone via README steps (ARCH-021, ARCH-029).
- [ ] **Realtime** completes EN→ES and ES→EN turns: voice in → translated voice out; source + target transcripts shown; **speech-end → first audio < 1.5s** observed in the demo (ARCH-010, ARCH-013).
- [ ] **Cascade** completes turns with **live streaming** — source partials render as produced, target tokens + TTS audio stream in before `tts.complete`; **end-to-end < 3s (target < 2s)** observed (ARCH-011, ARCH-013).
- [ ] Mode toggle switches modes between turns within one session (ARCH-007, ARCH-017 Flow G).
- [ ] Per-stage cascade latency + top-level latency display for both modes (ARCH-013).
- [ ] Cost/min estimate displays (labeled estimate) and the summary compares modes **and** model variants (both Realtime models; both translation models) (ARCH-014, ARCH-009 summary).
- [ ] WER Evaluation panel returns a score for a scripted phrase (ARCH-015).
- [ ] Session JSON persists with **no raw audio and no secrets** (sentinel test passes) (ARCH-016, ARCH-019).
- [ ] Backend tests green for cascade orchestration, provider boundaries, WER, cost, persistence, metrics, error sanitization, config (ARCH-020).
- [ ] 5-minute back-and-forth run: no disconnect, no audio drift/overlap, no memory leak (ARCH-020 preflight).
- [ ] Clean separation verified: mode-agnostic UI renders only from `sessionStore`/`UiSessionState`; mode-specific transports own all wire detail (PRD code-quality; ARCH-007).
- [ ] README + CLAUDE.md/AGENTS.md complete; **1–2 pp comparison write-up covers latency, quality (WER), cost, controllability, AND an evidence-backed recommendation for when each mode fits** (PRD must-have 8; ARCH-023).
- [ ] Git history is scoped per logical unit (no single "initial commit" dump) (ARCH-023).

---

## Phase A — Repo, Config & Domain Models

**Goal:** Stand up the .NET solution + Vite app skeleton, configuration/secrets, the pricing config, and the complete domain model so every later phase has a typed contract to build against.

**Spec anchors:** `ARCH-006`, `ARCH-028`, `ARCH-005`, `ARCH-007`, `ARCH-012`, `ARCH-014`, `ARCH-018`, `ARCH-029`, `ARCH-019`.

### A.1 — Solution + repo scaffold
- [x] `dotnet` solution with `AiInterpreter.Api` (ASP.NET Core Web API, net8.0) + `AiInterpreter.Tests` (xUnit) projects; Vite React-TS app under `web/`.
- [x] Directory layout matches `ARCH-006` exactly (Controllers/, Realtime/, Cascade/, Providers/{Abstractions,Deepgram,OpenAI,Fakes}/, Sessions/, Metrics/, Cost/, Evaluation/, Config/, Security/, Common/; web/src/{api,audio,realtime,cascade,state,components,types}/).
- [x] `.gitignore` excludes `.env`, `.env.local`, `server/**/bin/`, `server/**/obj/`, `web/node_modules/`, `data/sessions/*.json`; keep `data/sessions/.gitkeep`.
- [x] `config/pricing.json` and `docs/` (COMPARISON_WRITEUP.md, DEMO_SCRIPT.md placeholders) created.
- [x] Files: NEW — solution files, both csproj, `web/package.json`, `web/vite.config.ts`, `.gitignore`, `data/sessions/.gitkeep`, `config/pricing.json`.
- [x] Anchors: `ARCH-006`. Cross-doc invariant: none.

### A.2 — Configuration, secrets & Options classes
- [x] `.env.example` lists every variable from `ARCH-028` with comments, no real keys.
- [x] `Options` classes bound via `IOptions`: `RealtimeOptions`, `DeepgramOptions`, `OpenAiTranslationOptions`, `OpenAiTtsOptions`, `PricingOptions` — fields per `ARCH-012` "Provider Options (enumerated)".
- [x] Standard API keys load **server-side only**; nothing in this layer is exposed to the SPA.
- [x] Files: NEW — `Realtime/RealtimeOptions.cs`, `Providers/Deepgram/DeepgramOptions.cs`, `Providers/OpenAI/OpenAiOptions.cs` (translation + tts), `Cost/PricingOptions.cs`, `.env.example`. (`appsettings*.json` deliberately **unchanged** — inline defaults are the single source of truth; binding wired in A.5.)
- [x] Anchors: `ARCH-028`, `ARCH-012`, `ARCH-019`. Cross-doc invariant: extended (Options mirror env contract) — rows in Appendix A + `server/CLAUDE.md`.

### A.3 — Domain models (enums + records)
- [x] All enums + records from `ARCH-005` implemented exactly (`InterpretationMode`, `LanguageCode`, `TurnStatus`, `SessionStatus`, `LatencyStage`, `ClockSource`; `LanguageDirection`, `ProviderProfile`, `SessionConfig`, `InterpretationSession`, `ModeTransitionEvent`, `InterpretationTurn`, `TranscriptSegment`, `LatencyEvent`, `CostEstimate`, `SessionSummary`, `ModeSummary`, `WerSummary`, `EvaluationPhrase`, `WerResult`, `ProviderError`). _(verified zero drift vs ARCH-005/Appendix A; impl + code-quality reviewer diffed.)_
- [x] JSON serialization is **camelCase** (System.Text.Json options) so API/persisted JSON matches `ARCH-009`/`ARCH-016` examples. _(shared `Common/JsonDefaults`: camelCase + enum-as-camelCase-string + explicit-null; ISO-8601 `+00:00` ≡ `Z` — see ARCH-009 timestamp annotation.)_
- [x] `Common/Clock.cs` (`IClock` abstraction returning `DateTimeOffset` — injectable for deterministic tests) + `Common/Result.cs`.
- [ ] ~~Mirror the TS domain types in `web/src/types/domain.ts`~~ — **RE-SEQUENCED to Phase D** (D.1 typed-contract prereq; `web/` area + frontend build phase). See brief 003.
- [x] Files: NEW (backend) — `Sessions/SessionModels.cs`, `Providers/Abstractions/ProviderErrors.cs`, `Common/Clock.cs`, `Common/Result.cs`, `Common/JsonDefaults.cs`, + tests. _(`web/src/types/{domain,metrics}.ts` re-sequenced to Phase D.)_
- [x] Anchors: `ARCH-005`, `ARCH-007`. Cross-doc invariant: **NEW** — registered (domain-model row in `server/CLAUDE.md` cross-doc table; full inventory in Appendix A).

### A.4 — Pricing config + binding
- [x] `config/pricing.json` populated with the starting block from `ARCH-014` (Deepgram streaming `$0.0058`/min; both Realtime models; both translation models incl. `gpt-5.4-mini` marked CONFIRM-at-build; TTS bases); `version: "2026-05-28-payg-estimates"`; disclaimer present.
- [x] `PricingOptions` binds it via `PRICING_CONFIG_PATH`; missing config degrades to "estimate unavailable" (ARCH-018), never a crash. _(`Cost/PricingLoader` → `Result<PricingOptions>`; `File.Exists` + 1MB size guard + filtered catch; DI wiring of the path is A.5.)_
- [x] Files: extended — `config/pricing.json`, `Cost/PricingOptions.cs` (full shape, `SectionName` removed); NEW — `Cost/PricingLoader.cs`, `AiInterpreter.Tests/PricingConfigTests.cs`.
- [x] Anchors: `ARCH-014`, `ARCH-018`. Cross-doc invariant: extended (`PricingOptions` minimal→full; file-loaded).

### A.5 — Backend host, CORS, health + build/run wiring
- [x] `Program.cs` wires DI (Options + flat-env→section bridge + pricing-loader singleton + `JsonDefaults.Apply`), camelCase JSON, **CORS restricted to the local frontend origin** (`localhost:5173`, `WithOrigins` — never `AllowAnyOrigin`), WebSocket support enabled, listen port `5179`. Swagger Development-only.
- [x] `GET /api/health` → `{ "status": "ok" }` (minimal-API).
- [ ] ~~`web` dev server config (`VITE_API_BASE_URL` + Vite proxy)~~ — **RE-SEQUENCED to D.1** (frontend area/phase; D.1 builds the API clients + needs the proxy then). Secure-context note → README backlog (G.1). A.5's CORS already allows `localhost:5173`.
- [x] Files: extended — `Program.cs`, `Properties/launchSettings.json` (port 5179; template profiles dropped), `AiInterpreter.Tests.csproj`; NEW — `AiInterpreter.Tests/HostIntegrationTests.cs` (6, WebApplicationFactory). _(`/api/health` is minimal-API; `ConfigController` `/api/config` is B.9; `web/vite.config.ts` + `web/.env.example` → D.1.)_
- [x] Anchors: `ARCH-029`, `ARCH-019`. Cross-doc invariant: none.

### Acceptance criteria (A)
- [x] All A.X ticked (A.5's web Vite-config sub-item re-sequenced to D.1). `dotnet build` 0W/0E ✓; `npm run build` green (A.1; web unchanged since). Backend C# domain types compile ✓ (TS mirror re-sequenced to D, per A.3). `GET /api/health` returns ok ✓ (real host on 5179). No secrets in any committed file ✓ (security-confirmed). **Phase A COMPLETE.**

---

## Phase B — Backend Core Seams + Tests (no real providers)

**Goal:** Build and TDD every deterministic backend seam against **fake providers** — provider abstractions, the streaming cascade orchestrator, metrics, cost, WER, persistence, error sanitization, and session/config HTTP endpoints — so correctness is pinned before any paid API exists.

**Spec anchors:** `ARCH-012`, `ARCH-011`, `ARCH-013`, `ARCH-014`, `ARCH-015`, `ARCH-016`, `ARCH-008`, `ARCH-009`, `ARCH-018`, `ARCH-019`, `ARCH-020`.

### B.1 — Provider interfaces + event types
- [ ] `ISttProvider`, `ITranslationProvider`, `ITtsProvider` and their event hierarchies + request records + `AudioFrame` exactly per `ARCH-012` (IAsyncEnumerable streaming contracts; `CancellationToken` on each).
- [ ] `ProviderError` mapping helpers (exception/HTTP-status → code/retryable) per the `ARCH-012` mapping table (logic only; real providers wire it in C).
- [ ] Files: NEW — `Providers/Abstractions/ISttProvider.cs`, `ITranslationProvider.cs`, `ITtsProvider.cs`, `ProviderEvents.cs`, `AudioFrame.cs`.
- [ ] Anchors: `ARCH-012`. Cross-doc invariant: **NEW** (interface contracts → Appendix A).

### B.2 — Fake providers (all variants)
- [ ] `FakeSttProvider` (success-with-partials, empty-final, partials-then-error), `FakeTranslationProvider` (token-stream-then-final, immediate-final-only, error), `FakeTtsProvider` (chunked-then-complete, complete-only, error) — each emits ordered events with a **configurable delay before each event** via `yield return` + awaited delays and **honors `CancellationToken`** per `ARCH-012`.
- [ ] Files: NEW — `Providers/Fakes/Fake{Stt,Translation,Tts}Provider.cs`.
- [ ] Anchors: `ARCH-012`, `ARCH-020`. Cross-doc invariant: none.

### B.3 — Latency model + MetricsAggregator (+ tests)
- [ ] `LatencyEventFactory` stamps `LatencyEvent` with `clockSource` + `relativeMs` from the documented origin (ARCH-013 clock rules); `MetricsAggregator` computes universal + cascade + realtime metrics and the MUST/nice tiers (nice → `n/a`, never error).
- [ ] Tests: `MetricsAggregatorTests` — MUST-pair `relativeMs` formulas incl. **cross-clock** pairs; missing nice-tier event → `n/a`.
- [ ] Files: NEW — `Metrics/LatencyEventFactory.cs`, `Metrics/MetricsAggregator.cs`, `AiInterpreter.Tests/MetricsAggregatorTests.cs`.
- [ ] Anchors: `ARCH-013`, `ARCH-020`. Cross-doc invariant: extended (`LatencyEvent` gains `clockSource`).

### B.4 — Streaming cascade orchestrator (+ tests, with fakes)
- [ ] `CascadeStreamingOrchestrator` drives `STT partials/finals → per-finalized-segment translation (streamed) → TTS (streamed)` exactly per `ARCH-011` "Streaming pipeline", stamping `stt.*`/`translation.*`/`tts.*` LatencyEvents on **first arrival** of each event type and emitting normalized `TranscriptSegment`s + audio chunks to an output channel.
- [ ] Implements: empty-transcript short-circuit (`cascade.empty_transcript`, no translation/TTS), partial-failure rules (translation-fails → source kept; TTS-fails → both transcripts kept), and per-stage timeout/cancellation via linked CTS + `CancelAfter` (ARCH-012/ARCH-018).
- [ ] Tests: `CascadeOrchestratorTests` (CRITICAL) — success path + stage ordering; **empty-transcript short-circuit (translation/TTS NEVER invoked via call-count spies; Status=Failed)**; two partial-failure cases; timeout → `*.timeout` event + retryable error.
- [ ] Files: NEW — `Cascade/CascadeStreamingOrchestrator.cs`, `Cascade/CascadeModels.cs`, `AiInterpreter.Tests/CascadeOrchestratorTests.cs`.
- [ ] Anchors: `ARCH-011`, `ARCH-012`, `ARCH-013`, `ARCH-018`, `ARCH-020`. Cross-doc invariant: none.

### B.5 — Cost estimator (+ tests)
- [ ] `CostEstimator` loads pricing, **branches on pricing basis** (audio-minute vs token vs character), converts realtime audio-seconds→tokens, computes per-turn + per-minute cost, emits `CostEstimate` with assumptions + `pricingConfigVersion`.
- [ ] Tests: `CostEstimatorTests` (IMPORTANT) — deterministic per-basis estimates (Deepgram min, OpenAI tokens for translation, gpt-4o-mini-tts audio-output tokens, tts-1 chars); missing pricing → estimate unavailable.
- [ ] Files: NEW — `Cost/CostEstimator.cs`, `AiInterpreter.Tests/CostEstimatorTests.cs`.
- [ ] Anchors: `ARCH-014`, `ARCH-020`. Cross-doc invariant: none.

### B.6 — WER calculator + phrase store (+ tests)
- [ ] `WerCalculator` (DP edit distance over normalized word arrays: lowercase → strip punctuation → normalize whitespace; `WER=(S+I+D)/N`); `EvaluationPhraseStore` loads `evaluation-phrases.json` (8–12 EN+ES phrases).
- [ ] Tests: `WerCalculatorTests` (CRITICAL) — perfect=0; one deletion; one insertion; one substitution; empty hypothesis; **empty reference (N=0) rejected (no divide-by-zero)**; punctuation/casing normalization.
- [ ] Files: NEW — `Evaluation/WerCalculator.cs`, `Evaluation/EvaluationPhraseStore.cs`, `Evaluation/evaluation-phrases.json`, `AiInterpreter.Tests/WerCalculatorTests.cs`.
- [ ] Anchors: `ARCH-015`, `ARCH-020`. Cross-doc invariant: none.

### B.7 — Session store, persistence writer, summary (+ tests)
- [ ] `SessionStore` (in-memory), `SessionPersistenceWriter` (write-on-end MUST + best-effort per-turn; filename `session_YYYYMMDDTHHMMSSZ_<short-id>.json`; **path-traversal guard** — server-generated id `^[A-Za-z0-9_-]+$`, resolved path stays under `SESSION_DATA_DIR`), `SessionSummaryService` (computes `SessionSummary` on demand).
- [ ] Tests: `SessionPersistenceTests` (IMPORTANT) — round-trip; **sentinel assertion that JSON contains neither standard key, nor ephemeral secret, nor raw audio** (invariants 6/6b/8); transcripts/latency present; `../` sessionId rejected.
- [ ] Files: NEW — `Sessions/SessionStore.cs`, `Sessions/SessionPersistenceWriter.cs`, `Sessions/SessionSummaryService.cs`, `AiInterpreter.Tests/SessionPersistenceTests.cs`.
- [ ] Anchors: `ARCH-016`, `ARCH-008`, `ARCH-019`, `ARCH-020`. Cross-doc invariant: none.

### B.8 — Error sanitizer (+ tests)
- [ ] `ErrorSanitizer` maps internal/provider errors → safe `ProviderError`/`UiError` (no stack traces, no secrets); preserves `HttpStatusCode`/`Retryable`; logs original server-side only.
- [ ] Tests: `ErrorSanitizerTests` — exception containing an API-key substring + stack → `SafeMessage` contains neither; status/retryable preserved.
- [ ] Files: NEW — `Security/ErrorSanitizer.cs`, `AiInterpreter.Tests/ErrorSanitizerTests.cs`.
- [ ] Anchors: `ARCH-019`, `ARCH-018`, `ARCH-020`. Cross-doc invariant: none.

### B.9 — Session + Config HTTP endpoints (+ config tests)
- [ ] `SessionsController`: `POST /api/sessions`, `GET /api/sessions/{id}`, `POST …/end`, `GET …/summary`, `POST …/turns`, `POST …/turns/{turnId}/complete`, `POST …/turns/{turnId}/events` — request/response JSON exactly per `ARCH-009` (camelCase, nested config/direction/providerProfile). Backend owns `turnId` for all modes.
- [ ] `ConfigController`: `GET /api/config` reports configured booleans + model lists from **key presence only** (never values) per `ARCH-009`.
- [ ] **Global sanitizing exception handler (safety; origin A.5).** Wire `UseExceptionHandler` → normalized `UiError` via B.8's `ErrorSanitizer` (no stack traces / no secrets; ARCH-018/019), with/before these endpoints (A.5 has no throwing route yet, so no current exposure). Map `Result`/`Result<T>` → response DTOs (never serialize `Result` directly; origin A.3). Enforce sane collection-size bounds on request bodies (`Transcripts`/`LatencyEvents`/`Metadata`/`Units` are unbounded at the model level; origin A.3).
- [ ] Tests: `ConfigEndpointTests` (IMPORTANT) — `configured=false` when a key is absent; no secret echo.
- [ ] Files: NEW — `Controllers/SessionsController.cs`, `Config/ConfigService.cs`, `AiInterpreter.Tests/ConfigEndpointTests.cs`; extended — `Controllers/ConfigController.cs`.
- [ ] Anchors: `ARCH-009`, `ARCH-018`, `ARCH-019`, `ARCH-020`. Cross-doc invariant: extended (DTOs → Appendix A).

### B.10 — Provider boundary tests (against fakes)
- [ ] **NEW-create** `AiInterpreter.Tests/ProviderBoundaryTests.cs` (CRITICAL per ARCH-020) exercising the B.1 interfaces + B.2 fakes: ordered streaming-event contracts for `Fake{Stt,Translation,Tts}` (Started → Partial/first-token/chunk → Final/Complete); exception→`ProviderError` mapping helper (429 → `<stage>.rate_limited` retryable; timeout → `<stage>.timeout`; empty-final → `cascade.empty_transcript`); cancellation honored. No real keys. (Real-provider/HTTP-mock cases are added "extended" in C.5.)
- [ ] Files: NEW — `AiInterpreter.Tests/ProviderBoundaryTests.cs`.
- [ ] Anchors: `ARCH-012`, `ARCH-020`. Cross-doc invariant: none.

### Acceptance criteria (B)
- [ ] All B.X ticked. **All backend tests green** (orchestrator, provider boundary via fakes, WER, cost, persistence, metrics, sanitizer, config). Invest per the ARCH-020 priority tiers — `MetricsAggregatorTests`/`ErrorSanitizerTests` are THIN-IF-NEEDED. Session can be created/ended/persisted via HTTP using fake providers end-to-end. No real provider keys required to run tests.

---

## Phase C — Cascade Real Providers + Streaming Transport

**Goal:** Implement the real Deepgram/OpenAI providers behind the Phase-B interfaces and the streaming WebSocket endpoint (+ blob fallback), so a real cascade turn streams end-to-end. **Highest-risk integration #1.**

**Spec anchors:** `ARCH-011`, `ARCH-012`, `ARCH-009`, `ARCH-008`, `ARCH-018`, `ARCH-019`, `ARCH-030`, `ARCH-020`.

### C.1 — DeepgramSttProvider (live WS + pre-recorded fallback)
- [ ] NuGet `Deepgram` (v6.6.x). Live path: `CreateListenWebSocketClient`, `model=nova-3`, `language=multi`, `smart_format=true`, `interim_results=true`, `encoding=linear16`, `sample_rate=<from start msg>`, `channels=1`, `utterance_end_ms`; emit `SttStarted → SttPartial* → SttFinal` per segment.
- [ ] Fallback path: `CreateListenRESTClient` on a blob → single `SttFinal` (no interim; `stt.first_partial` = `n/a`); Deepgram auto-detects container — **no transcoding**.
- [ ] Exception/HTTP → `ProviderError` per the `ARCH-012` mapping table (429/401/403/400/5xx/empty/timeout).
- [ ] Files: NEW — `Providers/Deepgram/DeepgramSttProvider.cs`.
- [ ] Anchors: `ARCH-011`, `ARCH-012`, `ARCH-030`. Cross-doc invariant: none.

### C.2 — OpenAiTranslationProvider (streamed, both models)
- [ ] Responses API `POST /v1/responses` with `stream=true`; map `response.created→Started`, first `response.output_text.delta→translation.first_token + Partial`, deltas→Partial, `response.completed→Final` (+ usage tokens). Set `reasoning_effort="minimal"`, `text.verbosity="low"`. Faithful-interpreter system instruction (output ONLY the translation). Model selectable (`gpt-5.4-nano` default, `gpt-5.4-mini`); record model used.
- [ ] Exception/HTTP → `ProviderError`.
- [ ] Files: NEW — `Providers/OpenAI/OpenAiTranslationProvider.cs`.
- [ ] Anchors: `ARCH-011`, `ARCH-012`. Cross-doc invariant: none.

### C.3 — OpenAiTtsProvider (chunk streaming)
- [ ] `POST /v1/audio/speech` with chunk-transfer streaming; `tts.first_audio` = first chunk; default `ResponseFormat=mp3` (config: wav/pcm); voice config-driven (`OPENAI_TTS_VOICE`, default `alloy`); guard 4096-char input cap (non-retryable).
- [ ] Emit `TtsStarted → TtsFirstAudio → TtsAudioChunk* → TtsComplete`; exception/HTTP → `ProviderError`.
- [ ] Files: NEW — `Providers/OpenAI/OpenAiTtsProvider.cs`.
- [ ] Anchors: `ARCH-011`, `ARCH-012`. Cross-doc invariant: none.

### C.4 — Cascade streaming WebSocket endpoint
- [ ] `WS /api/cascade/stream` implements the exact protocol in `ARCH-009`: client `start` (sessionId, turnId, direction, encoding, sampleRate, translationModel, ttsVoice) → binary PCM frames → `stop`; server emits `transcript`/`latency`/`audio`/`cost`/`error`/`done`. Wires real providers into `CascadeStreamingOrchestrator`; persists the turn.
- [ ] DI swaps fakes → real providers via config; the orchestrator code is unchanged from Phase B (seam proven).
- [ ] Two cascade entry points are explicit: `CascadeWebSocketEndpoint.cs` hosts `WS /api/cascade/stream`; `CascadeController.cs` hosts the blob HTTP route (C.5).
- [ ] **`Origin` validation (origin A.5).** The WS upgrade bypasses the CORS middleware, so `CascadeWebSocketEndpoint` validates the `Origin` header itself (reject non-allowed origins) rather than relying on the CORS policy.
- [ ] Files: NEW — `Cascade/CascadeWebSocketEndpoint.cs`, `Controllers/CascadeController.cs`; extended — `Program.cs` (DI binding real vs fake by env).
- [ ] Anchors: `ARCH-009`, `ARCH-011`. Cross-doc invariant: extended (WS message DTOs → Appendix A).

### C.5 — Cascade blob fallback endpoint
- [ ] `POST /api/cascade/turn` (multipart): pre-recorded STT path → streamed translation/TTS → single JSON response; **audio upload validation** (max ~10MB; content-type allow-list; violations → `cascade.invalid_audio` 413/415). Explicitly the documented non-streaming fallback.
- [ ] The pre-recorded fallback logic lives in `Cascade/CascadeOrchestrator.cs` (pre-recorded STT, then **reuses** the streamed translation/TTS path) — **NOT in the controller**, per ARCH-008 ("provider logic never lives in controllers") / ARCH-011.
- [ ] Tests: `ProviderBoundaryTests` extended — 429 → `rate_limited`(retryable), timeout → `*.timeout`, empty → `cascade.empty_transcript`, oversized upload → `cascade.invalid_audio` (real-provider/HTTP-mock cases).
- [ ] Files: NEW — `Cascade/CascadeOrchestrator.cs` (blob-fallback orchestrator); extended — `Controllers/CascadeController.cs`, `AiInterpreter.Tests/ProviderBoundaryTests.cs`.
- [ ] Anchors: `ARCH-009`, `ARCH-011`, `ARCH-008`, `ARCH-019`, `ARCH-018`, `ARCH-020`. Cross-doc invariant: none.

### Acceptance criteria (C)
- [ ] All C.X ticked. A real cascade turn over the WebSocket: source partials → target tokens → first TTS audio all arrive **before `tts.complete`**; per-stage LatencyEvents are real (non-synthetic); the turn persists. Boundary tests green. Blob fallback works. (Manual smoke with real keys; logic tests don't need keys.)

---

## Phase D — Frontend Core + Cascade Streaming UI

**Goal:** Build the SPA shell, session setup, audio capture (streaming PCM + blob fallback), the cascade streaming client, playback, and the live transcript/metrics/cost panels — so a user can run a streaming cascade turn end-to-end in the browser.

**Spec anchors:** `ARCH-007`, `ARCH-030`, `ARCH-009`, `ARCH-011`, `ARCH-013`, `ARCH-014`, `ARCH-017`, `ARCH-018`, `ARCH-020`, `ARCH-029`.

### D.1 — App shell, API clients, state store
- [ ] React+Vite shell; `sessionStore` holding `UiSessionState` (ARCH-007); API clients `sessionsApi`/`cascadeApi`/`configApi` (realtime/evaluation added later). `VITE_API_BASE_URL` wired.
- [ ] **Clean-separation invariant:** UI components render only from `sessionStore`/`UiSessionState` (ARCH-007); transports (cascade WS client, realtime client) own all wire detail; components never import transport-client internals.
- [ ] Files: NEW — `web/src/App.tsx`, `web/src/main.tsx`, `web/src/state/sessionStore.ts`, `web/src/api/{sessionsApi,cascadeApi,configApi}.ts`.
- [ ] Anchors: `ARCH-007`, `ARCH-029`. Cross-doc invariant: none.

### D.2 — Session setup, mode + model selectors, config-gating
- [ ] `SessionSetup` (label, direction, **realtime-model + translation-model selectors**) + `ModeToggle`; on load call `GET /api/config` and **disable unconfigured modes** + populate `providerHealth` (Flow A). ModeToggle disabled during recording/processing/playing.
- [ ] Files: NEW — `web/src/components/SessionSetup.tsx`, `ModeToggle.tsx`.
- [ ] Anchors: `ARCH-007`, `ARCH-009`, `ARCH-017`. Cross-doc invariant: none.

### D.3 — Audio capture controller (streaming PCM + blob fallback)
- [ ] Streaming: `getUserMedia` → `AudioContext` → `AudioWorkletNode` (Float32→Int16 linear16, ~20–50ms frames) → binary over WS; declare `encoding=linear16` + actual `sampleRate` (no resample). Fallback: `MediaRecorder` with **probed** mimeType (probe order per ARCH-030) → blob.
- [ ] Mic-permission-denied path → error status + Start disabled.
- [ ] Files: NEW — `web/src/audio/audioCaptureController.ts`, `web/src/audio/pcmWorklet.ts`.
- [ ] Anchors: `ARCH-030`, `ARCH-007`. Cross-doc invariant: none.

### D.4 — Cascade streaming client + recording controls
- [ ] `cascadeStreamClient` opens `WS /api/cascade/stream`, sends `start` → PCM frames → `stop`, and dispatches `transcript`/`latency`/`audio`/`cost`/`error`/`done` into the store. `RecordingControls` enforces the ARCH-007 transition table.
- [ ] Files: NEW — `web/src/cascade/cascadeStreamClient.ts`, `web/src/components/RecordingControls.tsx`.
- [ ] Anchors: `ARCH-009`, `ARCH-007`, `ARCH-011`. Cross-doc invariant: none.

### D.5 — Playback controller
- [ ] Streamed cascade audio → `MediaSource` progressive playback (mp3); **fallback** to assembled-blob `HTMLAudioElement` if MSE append fails. Stamp `playback.started` on the `playing` event. No overlapping playback.
- [ ] Files: NEW — `web/src/audio/playbackController.ts`.
- [ ] Anchors: `ARCH-030`, `ARCH-013`. Cross-doc invariant: none.

### D.6 — Transcript, metrics, cost panels (live)
- [ ] `TranscriptPanel` renders source + target **partials as they arrive, replaced by finals** (subscribes to store stream). `MetricsPanel` shows top-level + cascade stage breakdown + session averages, `n/a` for unavailable nice-tier metrics. `CostPanel` shows "Estimated cost/min" (qualified) + model used + assumptions tooltip.
- [ ] Files: NEW — `web/src/components/{TranscriptPanel,MetricsPanel,CostPanel}.tsx`.
- [ ] Anchors: `ARCH-007`, `ARCH-013`, `ARCH-014`. Cross-doc invariant: none.

### D.7 — Error banner + frontend state tests
- [ ] `ErrorBanner` renders sanitized `UiError` with actionable copy (mic-denied, STT/translation/TTS failure, persistence warning).
- [ ] Tests (light, PRD-aligned): mode-toggle disabled during recording/processing/playing; **mic-denied** (`getUserMedia` rejection → error status, Start disabled).
- [ ] Files: NEW — `web/src/components/ErrorBanner.tsx`, frontend test files for the two transitions.
- [ ] Anchors: `ARCH-007`, `ARCH-018`, `ARCH-020`. Cross-doc invariant: none.

### Acceptance criteria (D)
- [ ] All D.X ticked. In the browser: start a session, run a **streaming cascade** EN→ES turn — source transcript renders live, target tokens + audio stream in and play, per-stage latency + estimated cost/min display, the turn persists. Mode/direction/model selectors work; unconfigured modes are disabled. Frontend state tests green.

---

## Phase E — Realtime Mode

**Goal:** Implement the Realtime path: backend ephemeral-credential broker + event ingest, the browser WebRTC client with VAD-off manual turns, event normalization, connection lifecycle, and mode-switch/recovery flows. **Highest-risk integration #2.**

**Spec anchors:** `ARCH-010`, `ARCH-009`, `ARCH-007`, `ARCH-013`, `ARCH-014`, `ARCH-017`, `ARCH-018`.

### E.1 — Realtime client-secret broker
- [ ] `RealtimeClientSecretService` + `POST /api/realtime/client-secret`: calls **`POST https://api.openai.com/v1/realtime/client_secrets`** (GA) with the standard key; body per `ARCH-010` (session.type=realtime, model, instructions, `output_modalities:["audio"]`, `audio.input.turn_detection=null`, `audio.input.transcription.model=gpt-4o-transcribe`, `audio.output.voice`); map `value→clientSecret`, `expires_at→expiresAt`. **Never** `/v1/realtime/sessions`.
- [ ] Tests: token-failure path sanitized; one bounded auto-retry honoring `Retry-After` (only this path).
- [ ] Files: NEW — `Realtime/RealtimeClientSecretService.cs`, `Controllers/RealtimeController.cs`.
- [ ] Anchors: `ARCH-010`, `ARCH-009`, `ARCH-018`. Cross-doc invariant: none.

### E.2 — Realtime turn-events ingest
- [ ] `POST /api/sessions/{id}/turns/{turnId}/events` persists normalized events (with `clockSource`); turn created via `POST …/turns` first (backend-owned turnId).
- [ ] Files: extended — `Controllers/SessionsController.cs`.
- [ ] Anchors: `ARCH-009`, `ARCH-010`. Cross-doc invariant: none.

### E.3 — Browser WebRTC client
- [ ] `realtimeWebRtcClient`: mint secret → `RTCPeerConnection` + `oai-events` data channel → `addTrack` mic → `createOffer`/`setLocalDescription` → **POST `offer.sdp` to `https://api.openai.com/v1/realtime/calls`** (Bearer ephemeral, `Content-Type: application/sdp`, **no `?model=`**) → `setRemoteDescription(answer)`; remote audio via `ontrack`.
- [ ] Files: NEW — `web/src/realtime/realtimeWebRtcClient.ts`, `web/src/realtime/realtimeEvents.ts`, `web/src/api/realtimeApi.ts`.
- [ ] Anchors: `ARCH-010`, `ARCH-007`. Cross-doc invariant: none.

### E.4 — Manual turn control + event mapping + metrics
- [ ] After DC opens, `session.update turn_detection=null`. Start → `input_audio_buffer.clear` + stream mic; Stop → `input_audio_buffer.commit` + `response.create` (stamp `turn.recording.stopped`). Map GA events per `ARCH-010` table (`response.output_audio.delta`, `response.output_audio_transcript.delta`, `conversation.item.input_audio_transcription.delta/.completed`); render source+target transcripts (show "source unavailable" if input transcription off); report normalized events to backend.
- [ ] Files: extended — `web/src/realtime/*`, `web/src/components/TranscriptPanel.tsx`.
- [ ] Anchors: `ARCH-010`, `ARCH-013`. Cross-doc invariant: none.

### E.5 — Connection lifecycle, disconnect, recovery, mode-switch
- [ ] One persistent `RTCPeerConnection` across turns (60-min cap noted); teardown on End/mode-switch (stop tracks, close DC, close pc, release stream). On ICE failed/disconnected → persist `realtime.session.disconnected` + advise switch-to-Cascade. Track `expiresAt` → re-mint before expiry. **Flow G** (mode switch between turns; emit `ModeTransitionEvent`) + **Flow H** (refresh recovery; backend stale-session flush).
- [ ] Files: extended — `web/src/realtime/realtimeWebRtcClient.ts`, `web/src/state/sessionStore.ts`; extended backend stale-session flush in `Sessions/SessionStore.cs`.
- [ ] Anchors: `ARCH-010`, `ARCH-017`, `ARCH-018`. Cross-doc invariant: none.

### E.6 — Realtime model selection
- [ ] `OPENAI_REALTIME_MODEL` + UI selector choose `gpt-realtime`/`gpt-realtime-mini`; model recorded on the turn/providerProfile for cost comparison.
- [ ] Files: extended — `Realtime/RealtimeClientSecretService.cs`, `web/src/components/SessionSetup.tsx`.
- [ ] Anchors: `ARCH-010`, `ARCH-014`. Cross-doc invariant: none.

### Acceptance criteria (E)
- [ ] All E.X ticked. Realtime EN→ES and ES→EN turns complete: voice in → translated voice out; source + target transcripts shown; **speech-end→first-audio < 1.5s** observed; connection survives a multi-turn session; mode switch Realtime↔Cascade works within one session; disconnect is surfaced (not swallowed).

---

## Phase F — Evaluation, Summary & Comparison

**Goal:** Ship the committed standalone WER Evaluation panel + endpoints, and the comparison summary that aggregates by mode and by model variant.

**Spec anchors:** `ARCH-015`, `ARCH-009`, `ARCH-007`, `ARCH-014`, `ARCH-017`.

### F.1 — Evaluation endpoints
- [ ] `GET /api/evaluation/phrases`; `POST /api/evaluation/transcribe` (STT-only, no translation/TTS — returns hypothesis + sttProvider/model + latency); `POST /api/evaluation/wer` (returns full `WerResult` incl. normalized fields).
- [ ] Files: NEW — `Controllers/EvaluationController.cs`, `Evaluation/EvaluationService.cs`.
- [ ] Anchors: `ARCH-009`, `ARCH-015`. Cross-doc invariant: extended (DTOs → Appendix A).

### F.2 — Evaluation panel (standalone, MUST)
- [ ] `EvaluationPanel`: phrase selector, reference display, record+transcribe a phrase, WER result, "WER is STT-only" explanation (Flow D). Persists WER on the session.
- [ ] Files: NEW — `web/src/components/EvaluationPanel.tsx`, `web/src/api/evaluationApi.ts`.
- [ ] Anchors: `ARCH-007`, `ARCH-015`, `ARCH-017`. Cross-doc invariant: none.

### F.3 — Comparison summary
- [ ] `ComparisonSummary` consumes `GET /api/sessions/{id}/summary`: avg latency by mode, estimated cost/min by mode **and by model variant** (both realtime + both translation models), error counts, WER summary, turn counts.
- [ ] Files: NEW — `web/src/components/ComparisonSummary.tsx`.
- [ ] Anchors: `ARCH-009`, `ARCH-007`, `ARCH-014`. Cross-doc invariant: none.

### Acceptance criteria (F)
- [ ] All F.X ticked. WER panel returns a score for a scripted phrase and persists it. Summary compares modes and model variants from real session data. Cost panel reflects the model used.

---

## Phase G — Docs, Demo, Stability & Write-up

**Goal:** Make the project runnable, demoable, stable for 5 minutes, and produce the required documentation + comparison write-up.

**Spec anchors:** `ARCH-021`, `ARCH-023`, `ARCH-007`, `ARCH-010`, `ARCH-013`, `ARCH-014`, `ARCH-015`, `ARCH-019`, `ARCH-020`, `ARCH-029`, `ARCH-022`.

### G.1 — README
- [ ] Overview, architecture summary, local setup (clean clone → run), env vars (ARCH-028), run commands, demo script link, provider config, metric meanings, **cost-estimate disclaimer**, WER explanation, known limitations, secure-context note, "session JSON is sensitive — don't commit".
- [ ] Files: NEW — `README.md`.
- [ ] Anchors: `ARCH-023`, `ARCH-021`, `ARCH-029`, `ARCH-019`. Cross-doc invariant: none.

### G.2 — CLAUDE.md / AGENTS.md
- [ ] How the agent was directed; architecture-first workflow; constraints (no secrets to frontend; no raw audio; preserve provider interfaces; scoped commits).
- [ ] Files: NEW — `CLAUDE.md`, `AGENTS.md`.
- [ ] Anchors: `ARCH-023`. Cross-doc invariant: none.

### G.3 — Demo script
- [ ] `docs/DEMO_SCRIPT.md` per `ARCH-021` (2 Realtime + 2 Cascade EN→ES, switch direction, 1+1, switch translation model + repeat a cascade turn, 1 WER, show summary, open JSON, explain tradeoffs); include the suggested EN/ES phrases.
- [ ] Files: NEW — `docs/DEMO_SCRIPT.md`.
- [ ] Anchors: `ARCH-021`. Cross-doc invariant: none.

### G.4 — Stability validation + manual preflight
- [ ] Run the `ARCH-020` manual preflight; resource-cleanup discipline verified (stop tracks, single reused `AudioContext`, drained playback, disposed pc); **5-minute run: (1) no disconnect, (2) no audio drift/overlap, (3) no memory leak**. Log results.
- [ ] Runtime smoke for the PRD mic-denied path: deny mic permission → UI shows the recovery hint and Start is disabled (ARCH-018).
- [ ] Files: extended — frontend audio/realtime cleanup paths as needed.
- [ ] Anchors: `ARCH-020`, `ARCH-007`, `ARCH-010`. Cross-doc invariant: none.

### G.5 — Comparison write-up
- [ ] `docs/COMPARISON_WRITEUP.md` (1–2 pp) using **real measured values** from session JSON: what was built; measurement method + limitations (cross-clock skew, backend-measured TTS timing, estimate-not-billing cost, English-leaning ES TTS voice); Realtime vs Cascade latency/cost/quality(WER)/controllability; **recommendation (when each fits)**; the **time-to-onboard a new language pair** (PRD impact metric — a config/provider-capability change in Cascade vs a model-capability question in Realtime); limitations + next steps.
- [ ] Files: NEW — `docs/COMPARISON_WRITEUP.md`.
- [ ] Anchors: `ARCH-023`, `ARCH-013`, `ARCH-014`, `ARCH-015`. Cross-doc invariant: none.

### G.6 — Optional deployment notes (trim/nice)
- [ ] `ARCH-022` deployment notes only if local demo is stable + time remains; HTTPS requirement called out.
- [ ] Files: extended — `README.md` or `docs/`.
- [ ] Anchors: `ARCH-022`. Cross-doc invariant: none.

### Acceptance criteria (G)
- [ ] All G.X (except G.6 optional) ticked. Clean-clone run succeeds via README. Demo script runs through. 5-minute stability passes all three checks. Write-up makes a defensible, evidence-backed recommendation.

---

## Trims / Nice-to-Haves Catalog

Deferred items with come-back guidance (from `ARCH-025`):

- **Sub-utterance incremental translation** — translate mid-segment rather than per finalized STT segment. Belongs in `Cascade/CascadeStreamingOrchestrator.cs`; needs the STT→translation handoff rule changed + new metrics; cross-doc impact ARCH-011/ARCH-013.
- **Atomic persistence (temp→rename) + per-WER write granularity** — `Sessions/SessionPersistenceWriter.cs`; add durability tests. MUST stays write-on-end + best-effort per-turn.
- **Realtime auto-reconnect** (≤2 attempts; re-mint + rebuild) — `web/src/realtime/realtimeWebRtcClient.ts`; default is detect+advise-switch.
- **Optional AWS deployment** (G.6) — `ARCH-022`.
- **Second real provider per stage / DeepL / ElevenLabs / Deepgram Aura** — provider abstractions already allow it; add a new `I*Provider` impl + config.
- **Nice-tier metrics** (`stt.first_partial` on fallback, etc.) degrade to `n/a`.

**Fallbacks (use only if blocked — not trims):** Realtime backend WebSocket proxy (if WebRTC blocked); Cascade blob + Deepgram pre-recorded (if live streaming blocked) — already built in C.5 as the documented fallback.

---

## Decisions tabled

Owner decisions are **resolved** (reflected in ARCHITECTURE.md): full streaming cascade (MUST); both Realtime models; full standalone WER panel; both translation models switchable. Remaining are **build-time confirmations** (ARCH-027 §16), none blocking:

- Confirm `gpt-realtime`/`gpt-realtime-mini` access in the target OpenAI account (E.1).
- Confirm Realtime input-transcription deltas with the chosen config; else degrade source-transcript UI (E.4).
- Re-verify `pricing.json` values, esp. `gpt-5.4-mini` + realtime token-conversion factors (A.4).
- Confirm capture format/no-transcoding on target browsers (C.1/D.3).

---

## Log

_(Append-only, date-stamped.)_

- **2026-05-28 — Phase A COMPLETE** (`ef05ccb` → A.5). Stood up: .NET solution + Vite scaffold (A.1); provider config Options + `.env.example` (A.2); ARCH-005 domain model + shared `JsonDefaults` + Clock/Result (A.3); ARCH-014 pricing + degrade-safe loader (A.4); ASP.NET host wiring — DI/env-bridge/JSON/CORS/WebSockets/port 5179/`GET /api/health` (A.5). Backend builds 0W/0E (WAE), **28 tests green**, real host serves `/api/health`. Lessons §1–§4 banked; cross-doc invariants (Options + domain models + pricing) registered in Appendix A + `server/CLAUDE.md`. Re-sequenced to later phases: TS mirror types + Vite dev-config → D.1; global sanitizing exception handler → B.9 (safety, no current exposure); cascade WS `Origin` validation → C.4. Commit cadence: commit-as-we-go on `main`, local-only (no remote). **Next: Phase B** (backend core seams + tests against fakes).
