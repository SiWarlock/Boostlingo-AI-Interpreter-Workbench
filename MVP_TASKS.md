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

**PHASE C IN PROGRESS — real providers + cascade WS wired end-to-end (C.1/C.6/C.2/C.3/C.4a done).** Next: **C.4b (WS-boundary hardening)**, then **C.5 (blob fallback)** closes Phase C. ⏸ **Team cycled at the C.4a→C.4b boundary (2026-05-29, ACTION 75%); a FRESH team resumes at C.4b.**

- **Team cycled 2026-05-29 (C.4a→C.4b, context auto-cycle ACTION 75%)** — round-seal: this `docs(tasks)` commit at HEAD · last code commit: `fff1ec2` (C.4a endpoint; `accd567` C.4a security) · session doc: `004-2026-05-29-phase-c-c1-c4a.md` (`0d8f788`) · next-slice target: **C.4b**.

**Last landed:** Phase C C.1→C.4a (this round): C.1 Deepgram STT (`3e5e899`), C.6 Deepgram error-status fidelity (`f69a366`, pulled-forward), C.2 OpenAI translation (`fffa4c1`), C.3 OpenAI TTS (`19acc15`), C.4a cascade WS endpoint (`accd567` security + `fff1ec2` endpoint). **The three real cascade providers are wired end-to-end** — `WS /api/cascade/stream` drives Deepgram→OpenAI→OpenAI through the **unchanged** B.4 orchestrator; cost/persist/`turn.recording.*`/`LatencyStage.Overall` in place; production-reachable. **Suite 161→211 green** (verified at C.4a close; build 0W/0E; format clean). Lessons §1–§21 banked (§18–§21 this round). Push deferred (no remote).

**Open handoff items for the fresh C.4b team** (full detail in session doc `004` + the C.4b/C.5 tasks + lessons §18–§21):
- **C.4b (hardening — the immediate next slice):** bounded `Channel<AudioFrame>` (FIRM — G.4 5-min-no-leak tie); WS `Origin` validation (WS bypasses CORS); `TtsFirstAudio.ContentType` clamp (low); double-End/complete guards (WS + HTTP end-paths now collide); stream-without-terminal `<stage>.unknown` hardening; start-frame field validation + error-shape polish; `VoiceByLanguage` resolution + stale-comment fix; **unit-pin the multi-segment cost accumulation** (C.4a undercount fix is shell-only/manual-smoke). _(The `encoding` allowlist + `Overall` enum already landed in C.4a.)_
- **C.5:** blob fallback (`POST /api/cascade/turn` + `CascadeOrchestrator`); reuses the C.4a wiring; **collapses the 429 reconciliation** (C.6 resolved it — real Deepgram 429→`rate_limited`); + the C.4a-origin SafeMessage sentinel sweep + 0-byte-audio contract decision.
- **F.1 (security MEDIUM):** cap `POST /api/evaluation/wer` hypothesis length (~2000 chars → `400`) **before** `WerCalculator.Compute` (DP-matrix DoS); never surface `LoadError`.
- **Limitation → G.5 write-up + ARCH-014 note (DONE this round):** cascade **TTS cost uses a target-char proxy** (audio-mode reports no TTS usage); documented assumption, degrades cleanly, no clean fix.
- **Build-confirm (non-blocking):** `gpt-5.4-mini` pricing rates (still `0.0`); `RealtimeTokensPerAudioSecond = 50` factor; **+ the first-real-key smoke must confirm** the OpenAI Responses `usage` nesting + the cascade WS path (C.4/D demo checklist) — confirm against real billing/keys in C/D/E.
- See **Carry-forward** for the remaining backlog and **D.1** (TS mirror types + Vite dev-config, re-sequenced).

---

## Carry-forward to upcoming briefs

_Entries carry an origin marker `(origin: YYYY-MM-DD <slice-id>)`. Items that belong to a specific phase are inlined as task checkboxes there (not held here); only next-1–2-slice items live here._

_(Triaged at the 2026-05-29 C.4a-close `/orchestrate-end`: **1 inlined** — double-End/complete races → now a C.4b task checkbox (the WS end-path collides with HTTP `/end`·`/complete`); **2 deleted** — "verify provider SDK surfaces" (last-consumer C.3 reached; C.1–C.3 all verified; principle banked as lesson §19) + "shared in-band-error factory" (resolved — C.3's audio-mode has no in-band-error frame, no factory needed). 6 kept/spread below, last-consumer-marked. Prior Phase-B-close triage removed 3 resolved items.)_

- **Cost-estimate evidence-trail polish (origin: 2026-05-28 B.5)** — (a) `CostEstimate.Units` always writes `cachedAudioInputSeconds` (0 when unsupplied) → consider omitting the key when null so the persisted trail distinguishes "no cache" from "0s"; (b) `EstimateTranslation` substitutes 0 for null tokens (silent $0) vs STT's degrade-on-null → align or document. _(last-consumer-slice: C.5 — the blob path is the last cascade-cost consumer in Phase C.)_
- **Stronger model validation — allowlist (origin: 2026-05-28 B.9c-i, security follow-up)** — `POST /api/sessions` length-caps `RealtimeModel`/`TranslationModel` but does not **allowlist** them against the published catalog (`ConfigService`). Reject off-catalog model names at the create boundary (reuse the ConfigService catalogs). _(last-consumer-slice: a hardening pass / G; client selectors already constrain to the catalog — low priority.)_
- **Per-item request-string caps (origin: 2026-05-29 B.9c-ii, security MEDIUM — bounded)** — `[MaxLength]` caps collection *counts* (≤500) but not per-*item* string lengths (`LatencyEvent.Name`/`Metadata`, `TranscriptSegment.Text`). Bounded today by Kestrel's 30MB + the count caps; full caps need a design call (request DTOs vs domain-record attrs — prefer request DTOs). Low impact (single-trusted-user, ARCH-019). _(last-consumer-slice: a hardening pass / G.)_
- **Realtime turn cost + errors population (origin: 2026-05-29 B.9c-ii, → E)** — `POST …/complete` carries `audioDurationMs?/transcripts?/status?` only; a realtime turn's `CostEstimate` is **null until E wires `CostEstimator.EstimateRealtime`** (cost backend-owned, ARCH-014), and a failed turn's `Errors` population is E's (E.5). The summary already handles null cost. _(last-consumer-slice: E.4/E.5.)_
- **Confirm OpenAI Responses `usage` nesting on first real-key smoke (origin: 2026-05-29 C.2)** — C.2 reads usage at `response.usage.{input_tokens,output_tokens}` (snake_case), research-verified + fixture/impl agree, but NOT yet exercised against a live key. On the first real-key smoke (C.4/D), confirm; if OpenAI nests differently, `OpenAiTranslationMapping.ReadUsage` + the fixture adjust together (cost depends on it, ARCH-014). _(last-consumer-slice: D — the frontend brings the first real-key cascade smoke.)_
- **Dead `Stream` field on both OpenAi options (origin: 2026-05-29 C.2/C.3, cleanup)** — `OpenAiTranslationOptions.Stream` + `OpenAiTtsOptions.Stream` are never consulted (both providers ALWAYS stream). Harmless dead config. Cleanup: remove from both Options + the Appendix A/ARCH-012/cross-doc rows (a small **cross-doc** change, own atomic slice) OR comment them "ignored (provider always streams)". Not worth a dedicated slice; do at a G cleanup pass or fold the comment-only version into any slice already touching the Options. _(last-consumer-slice: G cleanup / opportunistic.)_

_(Phase-A close — earlier items closed or inlined to their phase. **Closed:** WAE-scope (decided A.5 — Api-only WAE, no `Directory.Build.props`); A.1 template reconciliation (done A.5 — port 5179, template profiles dropped, Swagger Dev-only). **Inlined to B.9:** collection-size bounding + `Result`→DTO mapping (origin A.3) + global sanitizing exception handler (origin A.5, safety). **Inlined to C.4:** cascade WS `Origin` validation (origin A.5).)_

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
- [x] `ISttProvider`, `ITranslationProvider`, `ITtsProvider` and their event hierarchies + request records + `AudioFrame` exactly per `ARCH-012` (IAsyncEnumerable streaming contracts; `CancellationToken` on each). _(verified exact-match vs ARCH-012 §9; impl + code-quality reviewer diffed.)_
- [x] `ProviderError` mapping helpers (exception/HTTP-status → code/retryable) per the `ARCH-012` mapping table (logic only; real providers wire it in C). _(`ProviderErrorMapper` — `Map`/`EmptyTranscript`/`Timeout`; SafeMessage fixed-per-code, never echoes `ex.Message`.)_
- [x] Files: NEW — `Providers/Abstractions/ISttProvider.cs`, `ITranslationProvider.cs`, `ITtsProvider.cs`, `ProviderEvents.cs`, `AudioFrame.cs`, `ProviderErrorMapper.cs`, `AiInterpreter.Tests/ProviderErrorMappingTests.cs`. (`ProviderErrors.cs` reused from A.3.)
- [x] Anchors: `ARCH-012`. Cross-doc invariant: **NEW** (interface contracts → Appendix A) — registered in `server/CLAUDE.md` cross-doc table.

### B.2 — Fake providers (all variants)
- [x] `FakeSttProvider` (success-with-partials, empty-final, partials-then-error), `FakeTranslationProvider` (token-stream-then-final, immediate-final-only, error), `FakeTtsProvider` (chunked-then-complete, complete-only, error) — each emits ordered events with a **configurable delay before each event** via `yield return` + awaited delays and **honors `CancellationToken`** per `ARCH-012`. _(variant-by-enum ctor + `delayPerEvent`/scripted payloads + scriptable real-code `ProviderError`; pacing+cancellation via shared `FakeStreaming.PaceAsync`.)_
- [x] Files: NEW — `Providers/Fakes/Fake{Stt,Translation,Tts}Provider.cs` + `Providers/Fakes/FakeStreaming.cs` (shared `PaceAsync`) + `AiInterpreter.Tests/FakeProvidersTests.cs` (13).
- [x] Anchors: `ARCH-012`, `ARCH-020`. Cross-doc invariant: none. _(lesson §6 — streaming-fake pattern.)_

### B.3 — Latency model + MetricsAggregator (+ tests)
- [x] `LatencyEventFactory` stamps `LatencyEvent` with `clockSource` + `relativeMs` from the documented origin (ARCH-013 clock rules); `MetricsAggregator` computes universal + cascade + realtime metrics and the MUST/nice tiers (nice → `n/a`, never error). _(factory: `Create`/`Stamp`(injected `IClock`), relativeMs round+clamp≥0; aggregator: absolute-`Timestamp` math — cross-clock-safe, no-clamp on skew, never throws.)_
- [x] Tests: `MetricsAggregatorTests` (8) — MUST-pair formulas incl. **cross-clock** pairs (+/- skew, aggregator-no-clamp pinned vs factory-clamp); missing nice-tier event → `n/a`; realtime tier; empty-input no-throw.
- [x] Files: NEW — `Metrics/LatencyEventNames.cs`, `Metrics/MetricsModels.cs` (`TurnMetrics`), `Metrics/LatencyEventFactory.cs`, `Metrics/MetricsAggregator.cs`, `AiInterpreter.Tests/MetricsAggregatorTests.cs`; extended — `Program.cs` (DI: `IClock`→`SystemClock` + factory + aggregator). _(brief 008.)_
- [x] Anchors: `ARCH-013`, `ARCH-020`. Cross-doc invariant: **none** — consumes the existing A.3 `LatencyEvent` (already carries `clockSource`); `TurnMetrics` is an area-local computed type (not persisted/wire this slice → Appendix A registration deferred until first serialized). _(lesson §7; ARCH-013 "Metric origins" note added this round.)_

### B.4 — Streaming cascade orchestrator (+ tests, with fakes)
- [x] `CascadeStreamingOrchestrator` drives `STT partials/finals → per-finalized-segment translation (streamed) → TTS (streamed)` exactly per `ARCH-011` "Streaming pipeline" (nested per-segment loop; concurrent sub-utterance interleaving deferred per ARCH-025), stamping `stt.*`/`translation.*`/`tts.*` LatencyEvents on **first arrival** of each event type (via B.3's `LatencyEventFactory.Stamp` — never synthesize/back-date; forbidden-pattern #3) and emitting a flat transport-agnostic `IAsyncEnumerable<CascadeOutputEvent>` (`Transcript`/`Latency`/`Audio`/`Error`/`Done`). _(B.3 origins: also emits the stage-start markers `cascade.audio.received`/`translation.started`/`tts.started` the metric origins measure from.)_
- [x] Implements: empty-transcript short-circuit (`cascade.empty_transcript`, no translation/TTS), partial-failure rules (translation-fails → source kept; TTS-fails → both transcripts kept), and per-stage timeout/cancellation via linked CTS + `CancelAfter` (ARCH-012/ARCH-018). _(STT = per-event idle timeout via arm/disarm; OCE filtered `when (!ct.IsCancellationRequested)` so caller-cancel ≠ stage-timeout — both reviewer-flagged + fixed in-slice; lesson §8.)_
- [x] Tests: `CascadeOrchestratorTests` (CRITICAL, 9) — success path + stage ordering; **empty-transcript short-circuit (translation/TTS NEVER invoked via call-count spies; Status=Failed)**; two partial-failure cases; timeout → `*.timeout` retryable; first-arrival stamping; **multi-segment per-segment streaming** + idle-timeout regression + caller-cancellation propagation.
- [x] Files: NEW — `Cascade/CascadeStreamingOrchestrator.cs` (nested per-segment loop), `Cascade/CascadeModels.cs` (`CascadeOutputEvent` + `CascadeStartParams`), `AiInterpreter.Tests/CascadeOrchestratorTests.cs`; extended — `Metrics/LatencyEventNames.cs` (+`stt.started`, ARCH-013 MUST event). _(brief 009; no `Program.cs` change — DI is C.4.)_
- [x] Anchors: `ARCH-011`, `ARCH-012`, `ARCH-013`, `ARCH-018`, `ARCH-020`. Cross-doc invariant: none (`CascadeOutputEvent`/`CascadeStartParams` area-local — Appendix A at C.4 when serialized). _(Step-9 arch notes added this round: ARCH-012 STT-idle-timeout, ARCH-011 turn-lifecycle stage-label.)_

### B.5 — Cost estimator (+ tests)
- [x] `CostEstimator` loads pricing, **branches on pricing basis** (audio-minute vs token vs character vs audio-output-tokens), converts realtime audio-seconds→tokens, computes per-turn + per-minute cost, emits `CostEstimate` with assumptions + `pricingConfigVersion`. _(per-stage methods + `EstimateCascadeTurn` composite — Provider="cascade", Model=translation-model-used, PricingBasis="composite"; degrade via `Result<CostEstimate>`; `0.0` rate estimates to 0 ≠ absent; `decimal` no-rounding. lesson §9.)_
- [x] Tests: `CostEstimatorTests` (IMPORTANT, 12) — deterministic per-basis estimates (Deepgram min, OpenAI tokens for translation, gpt-4o-mini-tts audio-output tokens + approx-minute fallback, tts-1 chars); realtime conversion + cached-rate (incl. mini fall-back); cascade composite; cost/min + zero-duration; missing pricing/usage/model → unavailable; `0.0`-rate-still-estimates.
- [x] Files: NEW — `Cost/CostEstimator.cs`, `Cost/CostModels.cs` (`CostUsage`), `AiInterpreter.Tests/CostEstimatorTests.cs`; extended — `Program.cs` (DI: `CostEstimator`, first consumer of the A.5 pricing singleton). _(brief 010.)_
- [x] Anchors: `ARCH-014`, `ARCH-020`. Cross-doc invariant: none (`CostUsage` area-local). _(Step-9 arch note: ARCH-014 "Estimator conventions" — `"composite"` basis value + `RealtimeTokensPerAudioSecond=50` estimate.)_

### B.6 — WER calculator + phrase store (+ tests)
- [x] `WerCalculator` (DP edit distance over normalized word arrays: invariant-lowercase → strip `\p{P}` punctuation → collapse whitespace, **accents preserved**; `WER=(S+I+D)/N`, S/I/D backtraced individually, tie-break match>sub>del>ins; **WER unbounded** >1.0; empty reference → `ArgumentException`); `EvaluationPhraseStore` (self-loading facade, degrade-don't-crash per lesson §3) loads `evaluation-phrases.json` (10: 5 EN + 5 ES, no intra-word punctuation).
- [x] Tests: `WerCalculatorTests` (CRITICAL, 13) — perfect=0; deletion; insertion; substitution; empty hypothesis; **empty reference (N=0) rejected (no divide-by-zero)**; punctuation/casing normalization; accent-preserve; combined S/I/D (tie-break-invariant); **WER>1.0**; phrase-store load / degrade / size-guard.
- [x] Files: NEW — `Evaluation/WerCalculator.cs`, `Evaluation/EvaluationPhraseStore.cs`, `Evaluation/evaluation-phrases.json`, `AiInterpreter.Tests/WerCalculatorTests.cs`; extended — `Program.cs` (DI: `WerCalculator` + `EvaluationPhraseStore`), `AiInterpreter.Api.csproj` + `AiInterpreter.Tests.csproj` (copy the data file), `.env.example` (`EVALUATION_PHRASES_PATH`). _(brief 011; lesson §10.)_
- [x] Anchors: `ARCH-015`, `ARCH-020`. Cross-doc invariant: extended (`EVALUATION_PHRASES_PATH` env → ARCH-028 + cross-doc row, written this round). _(Step-9 arch note: ARCH-015 tie-break + accent-preserve.)_

### B.7 — Session store, persistence writer, summary (+ tests)
**Split into B.7a (SAFETY: store+writer+sentinel, `9bfea76`) + B.7b (summary service, `2d6a591`) — both landed.**
- [x] `SessionStore` (in-memory), `SessionPersistenceWriter` (write-on-end MUST + best-effort per-turn; filename `session_YYYYMMDDTHHMMSSZ_<short-id>.json`; **path-traversal guard** — server-generated id `^[A-Za-z0-9_-]+$`, resolved path stays under `SESSION_DATA_DIR`), `SessionSummaryService` (computes `SessionSummary` on demand).
- [x] Tests: `SessionPersistenceTests` (IMPORTANT) — round-trip; **sentinel assertion that JSON contains neither standard key, nor ephemeral secret, nor raw audio** (invariants 6/6b/8); transcripts/latency present; `../` sessionId rejected. _(Defense-in-depth cross-check, origin B.1 + B.4 security reviews: the writer must never serialize streaming raw audio — `TtsAudioChunk.Bytes` **or** `CascadeOutputEvent.Audio.Bytes` — structurally safe today since the session model has no audio field, but the sentinel + writer review should confirm it explicitly.)_ ✓ + `SessionSummaryServiceTests` (B.7b).
- [x] Files: NEW — `Sessions/SessionStore.cs`, `Sessions/SessionPersistenceWriter.cs`, `Sessions/SessionSummaryService.cs`, `AiInterpreter.Tests/SessionPersistenceTests.cs` (+ `SessionSummaryServiceTests.cs`). _(B.7a security-reviewer PASS #1/#2/#3/#5; B.7b pure read-only aggregation reusing the B.3 `MetricsAggregator`.)_
- [x] Anchors: `ARCH-016`, `ARCH-008`, `ARCH-019`, `ARCH-020`. Cross-doc invariant: none.

### B.8 — Error sanitizer (+ tests)
**Landed (SAFETY, invariant #4, `0262ce1`).**
- [x] `ErrorSanitizer` maps internal/provider errors → safe `ProviderError`/`UiError` (no stack traces, no secrets); preserves `HttpStatusCode`/`Retryable`; logs original server-side only. _(safe-by-construction: fixed message per code, never interpolates `ex.Message`/`Result.Error`/stack; logs original single-lined — log-forgery hardening; projects `ProviderError`→`UiError`, owns the generic `Exception`/`Result` boundary. lesson §13.)_
- [x] Tests: `ErrorSanitizerTests` — exception containing an API-key substring + stack → `SafeMessage` contains neither; status/retryable preserved. _(+ `Result<T>` overload, B.9c-i.)_
- [x] Files: NEW — `Security/ErrorSanitizer.cs`, `Security/UiError.cs` (backend record, ARCH-007 mirror), `AiInterpreter.Tests/ErrorSanitizerTests.cs`. _(security-reviewer PASS #4.)_
- [x] Anchors: `ARCH-019`, `ARCH-018`, `ARCH-020`. Cross-doc invariant: **NEW** `UiError` backend record (Appendix A + `server/CLAUDE.md` cross-doc row, written this round).

### B.9 — Session + Config HTTP endpoints (+ config tests)
**Split into B.9a (SAFETY #4 global handler, `88a7f3e`) + B.9b (SAFETY #1 config, `278caf2`) + B.9c-i (session endpoints, `e39ec7b`) + B.9c-ii (turn endpoints, `9e0066b`) — all landed** (two distinct safety invariants can't share a commit + the controller is large).
- [x] `SessionsController`: `POST /api/sessions`, `GET /api/sessions/{id}`, `POST …/end`, `GET …/summary` (B.9c-i), `POST …/turns`, `POST …/turns/{turnId}/complete`, `POST …/turns/{turnId}/events` (B.9c-ii) — request/response JSON exactly per `ARCH-009` (camelCase, nested config/direction/providerProfile). Backend owns `turnId` for all modes. _(thin controller → `SessionService`; `/complete` is realtime-only — cascade persists via C.4 WS; `CompleteTurnRequest` shape documented into ARCH-009.)_
- [x] `ConfigController`: `GET /api/config` reports configured booleans + model lists from **key presence only** (never values) per `ARCH-009`. _(B.9b; security-reviewer PASS #1.)_
- [x] **Global sanitizing exception handler (safety; origin A.5).** Wire `UseExceptionHandler` → normalized `UiError` via B.8's `ErrorSanitizer` (no stack traces / no secrets; ARCH-018/019). Map `Result`/`Result<T>` → response DTOs (never serialize `Result` directly; origin A.3). Enforce sane collection-size bounds on request bodies (`Transcripts`/`LatencyEvents`/`Metadata` — count caps via DataAnnotations; origin A.3). _(B.9a thin `IExceptionHandler` PASS #4; collection-COUNT caps landed — per-ITEM string caps deferred to carry-forward, bounded by Kestrel 30MB.)_
- [x] Tests: `ConfigEndpointTests` (IMPORTANT) — `configured=false` when a key is absent; no secret echo. _(+ `SessionsControllerTests`, `GlobalExceptionHandlerTests`.)_
- [x] Files: NEW — `Controllers/SessionsController.cs`, `Config/ConfigService.cs` (+`IConfigService`), `Config/ConfigModels.cs`, `Sessions/SessionService.cs` (+`ISessionService`), `Sessions/SessionDtos.cs`, `Security/GlobalExceptionHandler.cs`, `Security/ResultExtensions.cs`, `Controllers/ConfigController.cs`, + tests. _(new codes `session.not_found`/`turn.not_found`; framework 404/405 → ProblemDetails accepted for unrouted paths.)_
- [x] Anchors: `ARCH-009`, `ARCH-018`, `ARCH-019`, `ARCH-020`. Cross-doc invariant: extended (DTOs → Appendix A; `session.not_found`/`turn.not_found` → ARCH-018 — written this round).

### B.10 — Provider boundary tests (against fakes)
**Landed (`759cac2`).**
- [x] **NEW-create** `AiInterpreter.Tests/ProviderBoundaryTests.cs` (CRITICAL per ARCH-020) exercising the B.1 interfaces + B.2 fakes at the **interface level** (provider-agnostic — C.5 extends the same file with real providers). Scoped tight (6): net-new = **error-code preservation** (a `*Failed` carries the real `ProviderError` Code/Retryable/Stage/Provider through the boundary) + interface-level success-ordering anchors. Cancellation (B.2) + the mapper truth table (B.1) referenced, not re-tested. No real keys. (Real-provider/HTTP-mock + real-provider cancellation cases added "extended" in C.5.) _(lesson §17.)_
- [x] Files: NEW — `AiInterpreter.Tests/ProviderBoundaryTests.cs`.
- [x] Anchors: `ARCH-012`, `ARCH-020`. Cross-doc invariant: none.

### Acceptance criteria (B)
- [x] All B.X ticked. **All backend tests green** (orchestrator, provider boundary via fakes, WER, cost, persistence, metrics, sanitizer, config). Invest per the ARCH-020 priority tiers — `MetricsAggregatorTests`/`ErrorSanitizerTests` are THIN-IF-NEEDED. Session can be created/ended/persisted via HTTP using fake providers end-to-end. No real provider keys required to run tests. **Verified 2026-05-29: full suite `dotnet test` 161/161 green; build 0W/0E; `dotnet format` clean. Phase B COMPLETE.**

---

## Phase C — Cascade Real Providers + Streaming Transport

**Goal:** Implement the real Deepgram/OpenAI providers behind the Phase-B interfaces and the streaming WebSocket endpoint (+ blob fallback), so a real cascade turn streams end-to-end. **Highest-risk integration #1.**

**Spec anchors:** `ARCH-011`, `ARCH-012`, `ARCH-009`, `ARCH-008`, `ARCH-018`, `ARCH-019`, `ARCH-030`, `ARCH-020`.

### C.1 — DeepgramSttProvider (live WS + pre-recorded fallback)
**Landed (`<C.1 hash>` — set at /orchestrate-end).** Thin manual-smoke transport shell over a pure unit-TDD'd `DeepgramSttMapping`; `Channel<SttEvent>` callback bridge; one `ISttProvider` seam routes live-WS vs REST by `SttRequest.Encoding`. **NOT yet DI-wired** (consumers C.4 WS + C.5 blob — intentional B.1/B.2 seam-then-consumer pattern). 10 tests; suite 161→171. _(lesson §18; brief 020.)_
- [x] NuGet `Deepgram` (**v6.6.1**). Live path: `CreateListenWebSocketClient`, `model=nova-3`, `language=multi`, `smart_format=true`, `interim_results=true`, `encoding=linear16`, `sample_rate=<from start msg>`, `channels=1`, `utterance_end_ms`; emit `SttStarted → SttPartial* → SttFinal` per segment. _(WS `v2.WebSocket.LiveSchema`; callback→IAsyncEnumerable via `Channel<T>`, cancellation completes writer + `Stop()`s socket.)_
- [x] Fallback path: `CreateListenRESTClient` on a blob → single `SttFinal` (no interim; `stt.first_partial` = `n/a`); Deepgram auto-detects container — **no transcoding**. _(pure `ParsePrerecorded(SyncResponse)`; `PreRecordedSchema` lives in `Models.Listen.v1.REST`.)_
- [x] Exception/HTTP → `ProviderError` per the `ARCH-012` mapping table (429/401/403/400/5xx/empty/timeout) via reused `ProviderErrorMapper`. _(**v6.6.1 limitation, hardening tracked C.6:** the SDK throws `DeepgramRESTException` **without** HTTP status on the common error path → a real 429/401/403 degrades to `stt.unknown` (non-retryable); status-bearing mapping only on the rare empty-body `HttpRequestException`. Pinned + documented as intended.)_
- [x] Files: NEW — `Providers/Deepgram/DeepgramSttProvider.cs` (shell), `Providers/Deepgram/DeepgramSttMapping.cs` (pure helpers), `AiInterpreter.Tests/DeepgramSttProviderTests.cs` (10); extended — `AiInterpreter.Api.csproj` (`Deepgram` 6.6.1 + `InternalsVisibleTo("AiInterpreter.Tests")`).
- [x] Anchors: `ARCH-011`, `ARCH-012`, `ARCH-030`. Cross-doc invariant: **none** (confirmed — `DeepgramOptions`/`ISttProvider`/`SttEvent`/`ProviderErrorMapper` all consumed unchanged). _(ARCH-011 STT-path + empty-transcript-ownership realization notes added this round.)_

### C.2 — OpenAiTranslationProvider (streamed, both models)
**Landed (`fffa4c1`).** Raw-`HttpClient` SSE streaming shell + pure `OpenAiTranslationMapping`; mock-`HttpMessageHandler` end-to-end TDD (lesson §20). **NOT yet DI-wired** (consumers C.4 per-segment + C.5). 10 tests; suite 175→185. _(brief 021.)_
- [x] Responses API `POST /v1/responses` with `stream=true`; map `response.created→Started`, first `response.output_text.delta→translation.first_token + Partial`, deltas→Partial, `response.completed→Final` (+ usage tokens). Set **nested** `reasoning.effort="minimal"`, `text.verbosity="low"` (top-level `reasoning_effort` is Chat-Completions-only; no `[DONE]`; usage snake_case `response.usage.input_tokens/output_tokens`). Faithful-interpreter system instruction (output ONLY the translation). Model selectable (`gpt-5.4-nano` default, `gpt-5.4-mini`); record model used.
- [x] Exception/HTTP → `ProviderError` via `ProviderErrorMapper` (status-bearing — no C.1 Q5 gap); in-band SSE `error` event → `translation.unknown`. Per-stage timeout covers the whole send + read loop.
- [x] Files: NEW — `Providers/OpenAI/OpenAiTranslationProvider.cs` (shell), `Providers/OpenAI/OpenAiTranslationMapping.cs` (pure), `AiInterpreter.Tests/OpenAiTranslationProviderTests.cs` (10).
- [x] Anchors: `ARCH-011`, `ARCH-012`. Cross-doc invariant: **none** (confirmed — Options/contracts/mapper unchanged). _(ARCH-011 SSE-shape realization note added this round; lesson §20.)_

### C.3 — OpenAiTtsProvider (chunk streaming)
**Landed (`19acc15`).** Raw-`HttpClient` binary chunk-streaming shell + pure `OpenAiTtsMapping`; mock-`HttpMessageHandler` end-to-end TDD (lesson §20 + binary addendum). **NOT yet DI-wired** (consumers C.4 + C.5). 8 tests; suite 185→193. _(brief 023.)_
- [x] `POST /v1/audio/speech` with chunk-transfer streaming + **`stream_format="audio"`** (gpt-4o-mini-tts defaults to `sse`/base64-in-SSE — load-bearing); `tts.first_audio` = genuine first chunk; default `ResponseFormat=mp3` (config: wav/pcm); voice config-driven (uses `request.Voice`); guard 4096-char input cap pre-call via `MapStatus(400)` → `tts.invalid_request` (non-retryable, no HTTP call).
- [x] Emit `TtsStarted (after 200) → TtsFirstAudio → TtsAudioChunk(seq from 0)* → TtsComplete` (ContentType from response header, format-map fallback); exception/HTTP → `ProviderError` via `ProviderErrorMapper` (status-bearing — no Q5 gap); §20 timeout-covers-read-loop + non-inverted OCE filter.
- [x] Files: NEW — `Providers/OpenAI/OpenAiTtsProvider.cs` (shell), `Providers/OpenAI/OpenAiTtsMapping.cs` (pure), `AiInterpreter.Tests/OpenAiTtsProviderTests.cs` (8).
- [x] Anchors: `ARCH-011`, `ARCH-012`. Cross-doc invariant: **none** (confirmed). _(ARCH-011 TTS `stream_format` realization note added this round; lesson §20 binary addendum.)_

### C.4a — Cascade streaming WebSocket endpoint (core)
**Landed (`accd567` encoding-allowlist [SECURITY] + `fff1ec2` endpoint wiring — 2 commits).** Production wiring: `WS /api/cascade/stream` reachable; C.1/C.2/C.3 now live behind the unchanged B.4 orchestrator. 18 tests; suite 193→211. _(brief 024; lesson §21.)_
- [x] `WS /api/cascade/stream` per `ARCH-009`: client `start` → binary PCM → `stop`; server `transcript`/`latency`/`audio`/`cost`/`error`/`done`. Wires real providers into the **unchanged** `CascadeStreamingOrchestrator` (via `Channel<AudioFrame>` bridge); persists the turn (best-effort, B.7). _(thin shell + pure `CascadeWsMapping`/`CascadeStartValidation`; single socket sender.)_
- [x] DI swaps fakes → real providers by **key-presence** (`ConfigService.HasKey`) + `USE_FAKE_PROVIDERS` override; orchestrator + interfaces unchanged (seam proven).
- [x] **`encoding` allowlist (SECURITY, own commit).** `{linear16,pcm}` ordinal closed set validated (+ malformed-JSON rejected) **before** building `CascadeStartParams` (ARCH-019 header-injection boundary). Invalid → `cascade.invalid_audio`. _(security-reviewer PASS all 5; #3 raw-audio-never-accumulated confirmed.)_
- [x] **`LatencyStage.Overall` (cross-doc).** Added to the enum; `turn.recording.*` (WS, server-clock) + `turn.completed` (orchestrator, re-stamped Capture→Overall) carry it. _(ARCH-005 + Appendix A written this round.)_
- [x] **Cost emission.** `EstimateCascadeTurn` from observable usage (STT recording-minutes + translation tokens via `translation.final` Metadata [FORK-1a, additive] + TTS char-proxy) → `cost` message; degrades wholesale. _(ARCH-014 note written this round; TTS char-proxy = G.5 disclosure.)_
- [x] Files: NEW — `Cascade/CascadeStartValidation.cs`, `Cascade/CascadeWsMapping.cs`, `Cascade/CascadeWebSocketEndpoint.cs`, + 2 test files; extended — `Program.cs` (DI swap + map WS), `Sessions/SessionModels.cs` (`Overall`), `Cascade/CascadeStreamingOrchestrator.cs` (`turn.completed` Overall + `translation.final` Metadata tokens), `MetricsAggregatorTests.cs` (stale fixture). _(`Controllers/CascadeController.cs` is C.5.)_
- [x] Anchors: `ARCH-009`, `ARCH-011`, `ARCH-019`, `ARCH-005`, `ARCH-013`, `ARCH-014`. Cross-doc invariant: **`LatencyStage.Overall`** + WS message DTOs → ARCH-005/Appendix A (written this round); `CascadeOutputEvent`/`CascadeStartParams` stayed **internal** (only the WS DTOs serialize).

### C.4b — Cascade WS-boundary hardening
> Deferred from C.4a (the core shipped clean; these are boundary-robustness follow-ups). **Cycle target: the C.4b → C.5 seam** (lead auto-cycles at ACTION). Origin/double-End touch security; the encoding allowlist already landed in C.4a.
- [ ] **`Origin` validation (origin A.5).** The WS upgrade bypasses CORS, so `CascadeWebSocketEndpoint` validates the `Origin` header itself (reject non-allowed origins).
- [ ] **`TtsFirstAudio.ContentType` clamp (security low, origin B.4).** Clamp/validate the provider-sourced content-type before serializing it into the WS `audio` message.
- [ ] **Bound the `Channel<AudioFrame>` (security medium, origin C.4a — FIRM, not optional).** The C.4a audio bridge is unbounded — a stalled STT consumer grows it for the session (ties to G.4 5-min-no-leak). Add a bounded channel / backpressure. _(Deferred from C.4a per the C.1 precedent + single-user window; firm here because audio is continuous + G.4-relevant.)_
- [ ] **Double-`End`/`complete` guards (origin B.9c-i/ii, LOW).** Now that the WS end-path can collide with the HTTP `/end`·`/complete`, guard the unguarded read-modify-persist races (carry-forward item comes due).
- [ ] **Stream-without-terminal hardening (origin B.4).** When a real STT/translation stream ends WITHOUT a terminal event — unreachable via fakes — the orchestrator yields `Done(Completed)` with a missing target + no error. Harden to a `<stage>.unknown` failure; add a real-provider test.
- [ ] **Start-frame field validation + error-shape polish (origin C.4a flags).** Missing `sessionId`/`sampleRate=0` validation; non-WS request → UiError-shaped 400 (not empty); malformed-JSON-vs-encoding-reject message precision (single `Invalid()` factory); fragmented/oversized (>16KB) start frame.
- [ ] **`VoiceByLanguage` resolution + stale comment (origin C.3).** Resolve the `OpenAiTtsOptions.VoiceByLanguage` map to the effective voice upstream of the `TtsRequest` (confirm the cascade WS `start.ttsVoice` path vs the map); update the stale "unused until C.3" comment in `Providers/OpenAI/OpenAiOptions.cs`.
- [ ] **Unit-pin the multi-segment cost accumulation (origin C.4a, test-gap #5).** Extract the per-segment token/char accumulation to a pure `CascadeWsMapping` helper + unit-test multi-segment summing (pins the C.4a undercount fix, currently shell-only/manual-smoke).
- [ ] Files: extended — `Cascade/CascadeWebSocketEndpoint.cs`, `Cascade/CascadeStreamingOrchestrator.cs`, `Cascade/CascadeWsMapping.cs`, `Sessions/SessionStore.cs` (double-end guard), `Providers/OpenAI/OpenAiOptions.cs` (comment).
- [ ] Anchors: `ARCH-009`, `ARCH-019`, `ARCH-018`, `ARCH-020`. Cross-doc invariant: none expected.

### C.5 — Cascade blob fallback endpoint
- [ ] `POST /api/cascade/turn` (multipart): pre-recorded STT path → streamed translation/TTS → single JSON response; **audio upload validation** (max ~10MB; content-type allow-list; violations → `cascade.invalid_audio` 413/415). Explicitly the documented non-streaming fallback. _(Belt-and-suspenders, origin C.1: the C.1 `DrainAsync` blob buffer has no provider-level size cap — the ~10MB controller cap here is the primary guard; a provider-level cap is optional defense-in-depth.)_
- [ ] The pre-recorded fallback logic lives in `Cascade/CascadeOrchestrator.cs` (pre-recorded STT, then **reuses** the streamed translation/TTS path) — **NOT in the controller**, per ARCH-008 ("provider logic never lives in controllers") / ARCH-011.
- [ ] Tests: `ProviderBoundaryTests` extended — 429 → `rate_limited`(retryable), timeout → `*.timeout`, empty → `cascade.empty_transcript`, oversized upload → `cascade.invalid_audio` (real-provider/HTTP-mock cases). _(**C.6 resolved the C.1 Q5 finding:** a real Deepgram 429/401/403 now maps to `rate_limited`/`auth` via err_code→`MapStatus` recovery — this test works as written. Only a Deepgram **5xx-with-body** still degrades to `stt.unknown` (accepted residual, see C.6).)_
- [ ] **Real-provider test sweeps (origin C.3):** (a) add a **SafeMessage sentinel** across the real-provider suites — assert the provider error body / `ex.Message` / `err_msg` never appears in any surfaced `ProviderError.SafeMessage` (establishes the invariant-#4 cross-check for real providers, mirroring the B.7a persistence sentinel); (b) **0-byte-audio TTS contract** — C.3 currently yields `TtsStarted → TtsComplete` (no `TtsFirstAudio`) for an empty body (acceptable for MVP — real TTS always returns audio); if a `TtsFirstAudio`-always or `TtsFailed` contract is wanted, pin it here.
- [ ] Files: NEW — `Cascade/CascadeOrchestrator.cs` (blob-fallback orchestrator); extended — `Controllers/CascadeController.cs`, `AiInterpreter.Tests/ProviderBoundaryTests.cs`.
- [ ] Anchors: `ARCH-009`, `ARCH-011`, `ARCH-008`, `ARCH-019`, `ARCH-018`, `ARCH-020`. Cross-doc invariant: none.

### C.6 — Real-provider hardening (origin C.1)
> Hardening backlog surfaced by C.1 (brief 020 Step-9). **User decision 2026-05-29: PULL THE Q5 ITEM FORWARD (option ii)** — error fidelity feeds the G.5 comparison write-up, so it's sequenced as the **immediate next slice after C.1, before C.2** (brief 022). The remaining two items stay deferred/lower-priority + non-acceptance-blocking (Phase C exits on "a real cascade turn streams end-to-end") — they may stay unchecked at Phase-C exit with a Log note.
- [x] **Deepgram error-status fidelity (Q5, MEDIUM) — DONE `f69a366` (brief 022 `deepgram_error_status_fidelity`; pulled forward, landed before C.2).** v6.6.1 throws `DeepgramException`/`DeepgramRESTException` with **no HTTP status** on the common error path, so a real 429/401/403 degrades to `stt.unknown` (non-retryable) — losing rate-limit retryability + auth distinction. Extract the status from the SDK exception → route through the ARCH-012 mapper (vendor-agnostic `ProviderErrorMapper.MapStatus`). **Cross-doc:** ARCH-012 note + Appendix A (flips C.1 test 8's expectation; no structural rework). Collapses the C.5 reconciliation note above (real Deepgram 429 → `rate_limited` once this lands). _(Step-1 reality: `ErrCode` = Deepgram `err_code` **string** (matched string→status). **Accepted residual:** 5xx-**with-body** → `stt.unknown` (Deepgram 5xx uses key `error_code` ≠ `err_code` → `ErrCode` stays at the SDK default `"Unknown Error Code"`; `ErrMsg`-matching rejected as fragile); 5xx-empty-body still → `upstream_unavailable`. 429/401/403/400 recovered = the user's stated goal. See brief 022.)_
- [ ] **Live-WS unbounded `Channel<SttEvent>` (LOW):** the C.1 live path bridges via an unbounded channel — a stalled consumer could grow it for a session. Bounded-channel backpressure is the fix; ARCH-019 single-user posture tolerates today. (Activates at C.4 when the live path is wired; stability-relevant at G.4.)
- [ ] **Connect/Stop race on very-fast cancellation (LOW):** a Connect/Stop race on immediate cancellation can drop a late `SttFailed` (no hang/crash; bounded).
- [ ] Anchors: `ARCH-012`, `ARCH-018`, `ARCH-019`. Cross-doc invariant: the Q5 item is **ARCH-012 mapper** when it lands.

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
- [ ] After DC opens, `session.update turn_detection=null`. Start → `input_audio_buffer.clear` + stream mic; Stop → `input_audio_buffer.commit` + `response.create` (stamp `turn.recording.stopped`). Map GA events per `ARCH-010` table (`response.output_audio.delta`, `response.output_audio_transcript.delta`, `conversation.item.input_audio_transcription.delta/.completed`); render source+target transcripts (show "source unavailable" if input transcription off); report normalized events to backend. _(B.3 origin: emit `realtime.session.connecting` at WebRTC connect-start — browser clock — so `realtime_connect_ms` computes; else it stays honest `n/a`.)_
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
- [ ] **Hypothesis length cap (SECURITY, origin B.6 security review — MEDIUM).** `POST /api/evaluation/wer` MUST cap the request-body hypothesis length (suggest ~2000 chars / ~500 words → `400 evaluation.invalid_phrase`) **before** invoking `WerCalculator.Compute` — the calculator allocates an `n×m` DP matrix, so an unbounded hypothesis is a memory-DoS surface once it's request-reachable. (The calculator itself is safe; the boundary is F.1's job — ARCH-019.)
- [ ] **`LoadError` never surfaced (security low, origin B.6 / B.5 family).** `EvaluationPhraseStore.LoadError` (and any `Result.Error`) embeds path/`ex.Message` fragments — the controller exposes only `isLoaded` / a fixed-safe message, never the raw error (pairs with B.8/B.9 sanitizer).
- [ ] Files: NEW — `Controllers/EvaluationController.cs`, `Evaluation/EvaluationService.cs`.
- [ ] Anchors: `ARCH-009`, `ARCH-015`, `ARCH-019`. Cross-doc invariant: extended (DTOs → Appendix A).

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
- Re-verify `pricing.json` values, esp. `gpt-5.4-mini` + realtime token-conversion factors (A.4). _(B.5 chose `CostEstimator.RealtimeTokensPerAudioSecond = 50` as the working estimate — commented as such + surfaced in every realtime estimate's `Assumptions`; confirm against OpenAI realtime audio-token billing before relying on realtime cost numbers. `gpt-5.4-mini` rates still `0.0`.)_
- Confirm capture format/no-transcoding on target browsers (C.1/D.3).

---

## Log

_(Append-only, date-stamped.)_

- **2026-05-28 — Phase A COMPLETE** (`ef05ccb` → A.5). Stood up: .NET solution + Vite scaffold (A.1); provider config Options + `.env.example` (A.2); ARCH-005 domain model + shared `JsonDefaults` + Clock/Result (A.3); ARCH-014 pricing + degrade-safe loader (A.4); ASP.NET host wiring — DI/env-bridge/JSON/CORS/WebSockets/port 5179/`GET /api/health` (A.5). Backend builds 0W/0E (WAE), **28 tests green**, real host serves `/api/health`. Lessons §1–§4 banked; cross-doc invariants (Options + domain models + pricing) registered in Appendix A + `server/CLAUDE.md`. Re-sequenced to later phases: TS mirror types + Vite dev-config → D.1; global sanitizing exception handler → B.9 (safety, no current exposure); cascade WS `Origin` validation → C.4. Commit cadence: commit-as-we-go on `main`, local-only (no remote). **Next: Phase B** (backend core seams + tests against fakes).

- **2026-05-28 — Phase B opened (B.1–B.2); team wound down at B.2 (context cycle).**
  - Planning-level: provider contracts + `ProviderErrorMapper` (B.1) and the three fake providers (B.2) landed against the B.1 interfaces — the deterministic substrate the rest of Phase B builds + tests against. **24 commits across A.1→B.2; 50 tests green; runnable host.**
  - Decisions: the error-mapper is the single owner of the ARCH-012 table with `SafeMessage` never echoing `ex.Message` (lesson §5); fakes emit **real** ARCH-012 error codes (not invented `*.failed`); the paced-async-iterator streaming-fake pattern (lesson §6).
  - Scope shifts: none new — earlier re-sequences hold (TS types + Vite → D.1; B.9 exception-handler + C.4 WS-`Origin` + B.7 `TtsAudioChunk` cross-check inlined to their phases).
  - **Cycle:** impl context hit **ACTION (76%)** at the B.2 boundary → **full team wind-down** (no successors spawned; the human restarts the team fresh). Clean break — nothing in flight, B.2 fully committed + documented.
  - Next session target: **B.3 (latency model + `MetricsAggregator`)**.
  - Reference: implementer session doc `001-2026-05-28-phase-a-plus-b1-b2.md`; briefs `001`–`007`; lessons §1–§6.

- **2026-05-28 — Phase B core seams (B.3–B.6) landed; crash-recovered round close.**
  - Built the four mid-Phase-B deterministic seams against fakes: metrics/latency layer (B.3 `620f542`), streaming cascade orchestrator (B.4 `9b679b1` — CRITICAL, the spec centerpiece), cost estimator (B.5 `af40aaa`), WER calculator + scripted-phrase store (B.6 `edcbacd`). Session doc `002` (`88049bb`). **50 → 92 tests green** (B.3 +8, B.4 +9, B.5 +12, B.6 +13).
  - Decisions: aggregate metrics from absolute `Timestamp` (cross-clock safe), `relativeMs` is display-only, factory-clamps-but-aggregator-doesn't (lesson §7); cascade = nested per-segment loop with per-event idle-timeout + OCE-filter caller-cancel split (lesson §8); cost branches on basis, a `0.0` rate ≠ absent config, cascade → one composite estimate (lesson §9); WER normalize + DP-backtrace S/I/D, unbounded, empty-ref precondition (lesson §10).
  - Hot-routed this round: ARCH-013 metric-origins · ARCH-012 STT-idle-timeout · ARCH-011 stage-label · ARCH-014 estimator-conventions · ARCH-015 WER-note · ARCH-028 `EVALUATION_PHRASES_PATH`; lessons §7–§10 + four `server/CLAUDE.md` index rows. Two precondition Findings folded into phase tasks: **C.4** `encoding` allowlist (MEDIUM) + **F.1** hypothesis-length cap (MEDIUM); related sanitizer-lows into B.8 / F.1. B.3 cross-doc invariant corrected to **none** (consumes existing A.3 `LatencyEvent`).
  - **Crash recovery:** predecessor orchestrator died mid-`/orchestrate-end` (machine shutdown). Slice commits (`620f542`/`9b679b1`/`af40aaa`/`edcbacd`) + session doc (`88049bb`) were already committed; only the round-seal (doc reconciliation) was outstanding. Successor verified the predecessor's uncommitted edits complete + correct, advanced Currently-in-progress → B.7a, appended this entry, and sealed the round.
  - Next session target: **B.7a (session store + persistence writer + sentinel — SAFETY)**; brief `012` drafted.
  - Reference: implementer session doc `002-2026-05-28-phase-b-b3-b6.md`; briefs `008`–`012`; lessons §7–§10.

- **2026-05-29 — Phase B COMPLETE (B.7a–B.10); fresh team, planned Phase-B-close cycle.**
  - Drove the fresh team from B.7a through Phase-B exit: B.7a session store + persistence writer + **safety sentinel** (`9bfea76`), B.7b summary service (`2d6a591`), B.8 error sanitizer + `UiError` (`0262ce1`), B.9a global sanitizing exception handler (`88a7f3e`), B.9b ConfigController (`278caf2`), B.9c-i session-lifecycle endpoints (`e39ec7b`), B.9c-ii turn-lifecycle endpoints (`9e0066b`), B.10 provider-boundary contract suite (`759cac2`). Session doc `003` (`30e2ba7`). **92 → 161 tests green** (full `dotnet test` verified at close; build 0W/0E; format clean). **Phase A + Phase B COMPLETE** — full session/turn/config HTTP surface + global sanitizing handler wired + reachable; all deterministic backend seams tested against fakes (no real keys).
  - Decisions: **B.9 split a/b/c-i/c-ii** for safety-commit isolation (two distinct safety invariants — #4 global handler, #1 config — can't share a commit; the controller is large + session-CRUD vs turn-lifecycle separable). Four **safety slices** (B.7a/B.8/B.9a/B.9b), each a mandatory `security-reviewer` PASS; **no safety finding escalated all round**. `UiError` backend record (ARCH-007 mirror) with server-only `[JsonIgnore] HttpStatusCode`; `ErrorSanitizer` safe-by-construction + log-forgery single-lining + `ForCode` for expected conditions; `/end` MUST-persist vs `/complete` best-effort (both 200-with-`persistenceWarning`, never 500); `PersistedPath` filename-only; controller `Result`→DTO never-serialize boundary; DataAnnotations on record **parameters**; `/complete` realtime-only (cascade persists via C.4 WS). Orchestrator caught + trimmed its own over-broad `CompleteTurnRequest` proposal at Step 2.5 (removed cost/WER/cascade fields — invariant #5 + ARCH-014 + realtime-only).
  - Hot-routed this round: **lessons §11–§17** (+ 7 `server/CLAUDE.md` index rows); ARCH-016 (filename/overwrite-in-place/sentinel-scope), ARCH-009 (summary pure-compute; session+turn endpoint realizations + `CompleteTurnRequest` spec-gap closed), ARCH-018 (sanitizer boundary + global handler + `session.not_found`/`turn.not_found` codes), Appendix A (`UiError` backend record + session/turn DTOs); `UiError` cross-doc row. Briefs `013`–`019` authored.
  - Scope shifts: none cut. Carry-forward triaged (see Step-5.5 below). The C.4 SECURITY-MEDIUM set + F.1 WER-cap remain inlined in their phase tasks.
  - New blockers: none. Build-confirms (`gpt-5.4-mini` pricing, realtime token factor) still pending real billing (C/E), non-blocking.
  - Next session target: **C.1 (`DeepgramSttProvider` — live WS + pre-recorded fallback)** — Phase C, real providers (highest-risk integration #1).
  - Reference: implementer session doc `003-2026-05-29-phase-b-b7-b10.md`; briefs `012`–`019`; lessons §11–§17.

- **2026-05-29 — Phase C real providers + cascade WS wired end-to-end (C.1/C.6/C.2/C.3/C.4a); fresh team, context auto-cycle at C.4a→C.4b.**
  - Drove the fresh Phase-C team through the three real cascade providers + the streaming WS integration: **C.1** Deepgram STT (`3e5e899`), **C.6** Deepgram error-status fidelity (`f69a366`), **C.2** OpenAI translation (`fffa4c1`), **C.3** OpenAI TTS (`19acc15`), **C.4a** cascade WS endpoint (`accd567` SECURITY encoding-allowlist + `fff1ec2` endpoint). Session doc `004` (`0d8f788`). **Suite 161→211 green** (+50; build 0W/0E; format clean). **Phase C's real cascade now streams end-to-end** — `WS /api/cascade/stream` drives the three real providers through the **unchanged** B.4 orchestrator (seam proven); C.1/C.2/C.3 production-reachable.
  - Decisions made: each real provider = a thin manual-smoke transport shell over a pure unit-TDD'd mapper (real network exempt per ARCH-020; deterministic surfaces TDD'd) — lessons §18 (real-provider pattern), §19 (SDK-hidden-status recovery), §20 (raw-HTTP/SSE + binary-chunk streaming), §21 (cascade WS endpoint). The "verify SDK/API surfaces by reflection, not docs" discipline caught a load-bearing reality on all three providers (Deepgram `err_code` string + 5xx `error_code` key; OpenAI nested `reasoning.effort`/no-`[DONE]`/snake_case usage; TTS `stream_format:"audio"` required) — each a would-be silent runtime failure. C.4a kept the orchestrator unchanged except minimal stamps (`turn.completed`→Overall, `translation.final` Metadata carries tokens for cost — FORK-1a). DI swap real↔fake by key-presence + `USE_FAKE_PROVIDERS`. `LatencyStage.Overall` added (cross-doc).
  - Scope shifts: **C.6 pulled forward (user decision)** — the Deepgram error-status fidelity hardening was sequenced AHEAD of C.2 (error fidelity feeds G.5; recover HTTP status from the SDK `err_code` → vendor-agnostic `MapStatus`, reused by C.3's input-cap guard). **C.4 split** into C.4a (core, landed) + **C.4b** (boundary hardening — bounded channel [firm], Origin, ContentType clamp, double-end, stream-without-terminal, start-frame validation, cost-accumulation unit-pin) — C.4b queued for the successor. Accepted residuals (documented, not silent): Deepgram **5xx-with-body→`stt.unknown`** (different JSON key; `ErrMsg`-match rejected as fragile); cascade **TTS cost target-char proxy** (audio-mode reports no usage) → G.5 disclosure + ARCH-014 note.
  - Hot-routed this round: **lessons §18–§21** (+4 `server/CLAUDE.md` index rows); **`LatencyStage.Overall`** → ARCH-005 + Appendix A; **`ProviderErrorMapper.MapStatus`** → Appendix A + ARCH-012 note; cascade WS DTOs → Appendix A; ARCH-011 (STT/TTS realizations, empty-transcript ownership, stage-label resolved), ARCH-013/014 (cascade cost-usage sourcing + TTS char-proxy), ARCH-009 (WS realization). Briefs `020`–`024` authored. Carry-forward triaged (see Step-5.5 in this commit).
  - New blockers: none. **First-real-key smoke still pending** for the cascade WS path + OpenAI usage-nesting confirm (C.4/D demo checklist) — non-blocking (logic is fake-tested; real network exempt per ARCH-020).
  - Next session target: **C.4b (cascade WS-boundary hardening)**; then **C.5 (blob fallback)** closes Phase C. Fresh team resumes at C.4b.
  - Reference: implementer session doc `004-2026-05-29-phase-c-c1-c4a.md`; briefs `020`–`024`; lessons §18–§21.
