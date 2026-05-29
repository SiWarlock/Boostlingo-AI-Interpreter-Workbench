# AI Interpreter Workbench `server/` — Build Guide

> **You're in `server/`.** This file plus root `CLAUDE.md` both load. The root file covers global project conventions + shared comm rules (track-prefix, escalation taxonomy, messaging budget); this file owns code-area conventions for the .NET backend.

## Launch protocol

| Working on... | cwd | Loads |
|---|---|---|
| Planning / docs / commits | repo root (`ai-interpreter-workbench/`) | root `CLAUDE.md` only |
| the .NET backend code | `server/` | this `CLAUDE.md` + root |

<!-- For a multi-area project, add a row per additional code area. -->

If you find yourself fighting the wrong conventions, check your cwd.

## Session start/end protocol

**At session start:**
1. Read `MVP_TASKS.md` (repo root) → "Currently in progress" section.
2. Confirm with the user what feature this session is targeting.
3. Read the relevant section of `ARCHITECTURE.md` from the lookup table below.

**At session end** (only when the user explicitly says we're done):

1. **Implementer runs `/session-end`.** Implementer writes ONLY:
   - `server/` code files (the slice's implementation)
   - test files (the slice's tests)
   - dependency manifest / lockfile (deps the slice adds)
   - `docs/sessions/<NNN>-<date>-<topic>.md` (session doc, created at `/session-end` Step 5)

   **Implementer must NOT touch (all orchestrator territory):**
   - `MVP_TASKS.md`
   - `server/LESSONS.md`
   - `server/CLAUDE.md` (entire file — both the Cross-doc invariants table AND the Lessons logged index)
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
| Domain model & lifecycle (records, enums, invariants) | `ARCHITECTURE.md` | §3 / ARCH-005 |
| Repository scaffold (project layout) | `ARCHITECTURE.md` | §5 / ARCH-006 |
| Backend architecture + layering | `ARCHITECTURE.md` | §5 / ARCH-008 |
| API contracts (sessions, turns, config, realtime token, cascade WS, evaluation) | `ARCHITECTURE.md` | §6 / ARCH-009 |
| Realtime broker (client_secrets mint, event ingest) | `ARCHITECTURE.md` | §7 / ARCH-010 |
| Cascade orchestrator + streaming pipeline | `ARCHITECTURE.md` | §8 / ARCH-011 |
| Provider interfaces, ProviderError, fakes, timeout/cancellation | `ARCHITECTURE.md` | §9 / ARCH-012 |
| Metrics / latency model + MUST/nice tiers | `ARCHITECTURE.md` | §10 / ARCH-013 |
| Cost estimation + pricing.json | `ARCHITECTURE.md` | §11 / ARCH-014 |
| WER evaluation | `ARCHITECTURE.md` | §12 / ARCH-015 |
| Persistence (JSON, path-traversal, write tiers) | `ARCHITECTURE.md` | §13 / ARCH-016 |
| Errors / normalized codes / 429; security boundaries | `ARCHITECTURE.md` | §15 / ARCH-018, ARCH-019 |
| Config & secrets; build & run contract | `ARCHITECTURE.md` | §15 / ARCH-028, ARCH-029 |
| Testing strategy + priority tiers | `ARCHITECTURE.md` | §15 / ARCH-020 |
| Model / contract inventory | `ARCHITECTURE.md` | Appendix A |
| Lessons logged (full prose) | `server/LESSONS.md` | by lesson # |

<!-- Seeded from the (complete) architecture doc. Add a row whenever a new topic is looked up twice. -->

## Stack

<!-- ▼ EXAMPLE BLOCK: stack quick-reference for implementer sessions. Canonical stack lives in root CLAUDE.md + ARCHITECTURE.md; this is the cheat sheet. ▼ -->

- **Runtime:** .NET 8 (C# 12)
- **Framework:** ASP.NET Core Web API
- **Validation:** System.Text.Json + record types (manual validation)
- **Lint / types / tests:** dotnet format + Roslyn analyzers / dotnet build (nullable enabled) / xUnit

<!-- ▲ END EXAMPLE BLOCK ▲ -->

## Standard commands

```bash
# Install deps (run once; re-run when the manifest changes)
dotnet restore

# Run the dev server (if applicable)
dotnet run --project server/AiInterpreter.Api

# Tests
dotnet test

# Quality
dotnet format --verify-no-changes
dotnet format whitespace --verify-no-changes
dotnet build

# Preflight (use before saying "done" with a feature)
dotnet format --verify-no-changes && dotnet build && dotnet test
```

## TDD protocol

**Write the failing test first.** Applies to deterministic code — see the TDD posture in root `CLAUDE.md` for what is test-first vs. exempt.

**Commit per slice when practical.** Never bundle a safety-critical slice with anything else.

## Forbidden patterns

Do not:

1. **Write code without a failing test first** (for deterministic code per the root TDD posture). Even one-line functions.
2. **Put provider logic in controllers / endpoints** — controllers delegate to application services; provider calls live behind `ISttProvider` / `ITranslationProvider` / `ITtsProvider` (ARCH-008). Keeps swapping a provider a contained change.
3. **Synthesize or back-date stage latency** — stamp each `LatencyEvent` on the *real* first arrival of its event type from the provider stream; never fabricate `stt.first_partial` / `translation.first_token` / `tts.first_audio` (ARCH-011/013). A buffered single response is not streaming.
4. **Collapse the translation stage to a blocking call** — `OpenAiTranslationProvider` streams tokens via the Responses API (`stream=true`); the orchestrator consumes the `IAsyncEnumerable` (ARCH-011/012).
5. **Let a standard API key, the ephemeral secret, or raw audio reach a response body or session JSON** — sanitize provider errors before returning them (root Key safety rules; ARCH-018/019).
6. **`git add -A` / `git add .`** — stage slice files explicitly (root push posture).

<!-- Accretes as lessons surface; each durable rule earns a LESSONS.md entry. -->

## Cross-doc invariants — schema/docs mirroring

Several typed models in this codebase are **contracts** mirrored in `ARCHITECTURE.md` and indexed in the table below. The architecture doc is the canonical contract; the model is the executable enforcement. Drift produces silent disagreement.

**Authoring discipline (orchestrator owns this table).** When the implementer adds, removes, or renames a field on one of these models, the implementer **flags it at Step 9 categorized as `Cross-doc invariant change`** per the routing matrix in `docs/orchestrator-briefing.md`. The implementer does NOT edit `server/CLAUDE.md` or `ARCHITECTURE.md` directly — the orchestrator writes the table row + the architecture edit hot during the same session. Working-tree state aligns within the round; commits stagger (implementer's slice commit lands code+tests; orchestrator's round commit lands the doc rows).

| Model | `ARCHITECTURE.md` section | Notes |
|---|---|---|
| `DeepgramOptions` | ARCH-012 / ARCH-028 (Appendix A) | Section `"Deepgram"`; inline defaults are source of truth; ApiKey backend-only (ARCH-019) |
| `OpenAiTranslationOptions` | ARCH-012 / ARCH-028 (Appendix A) | Section `"OpenAiTranslation"` |
| `OpenAiTtsOptions` | ARCH-012 / ARCH-028 (Appendix A) | Section `"OpenAiTts"`; optional `VoiceByLanguage` map |
| `RealtimeOptions` | ARCH-012 / ARCH-028 / ARCH-019 (Appendix A) | Section `"Realtime"`; ApiKey backend-only |
| `PricingOptions` | ARCH-014 (Appendix A) | Full ARCH-014 shape (A.4); **file-loaded via `PRICING_CONFIG_PATH`** (not section-bound; no SectionName); `realtime` explicit class (estimatorNote string sibling), `translation`/`tts` model-keyed dicts |
| ARCH-005 domain model — 6 enums + 15 records | ARCH-005 / Appendix A | `InterpretationSession`/`InterpretationTurn`/`TranscriptSegment`/`LatencyEvent`(+ARCH-013)/`CostEstimate`(+ARCH-014)/`SessionSummary`·`ModeSummary`·`WerSummary`(+ARCH-009)/`ProviderError`(+ARCH-012)/`EvaluationPhrase`·`WerResult`(+ARCH-015)/enums/etc. Full field inventory in Appendix A; a field change pairs with the ARCH-005 edit. Serialized via `Common/JsonDefaults` (camelCase + enum-as-string + explicit-null). |
| Provider contracts — `ISttProvider`/`ITranslationProvider`/`ITtsProvider` + event hierarchies + request records + `AudioFrame` | ARCH-012 / Appendix A | Verbatim ARCH-012 §9 (B.1); consumed by B.4, implemented by B.2 fakes / C real providers. `ProviderErrorMapper` owns the exception→`ProviderError` table (SafeMessage never echoes `ex.Message`). |

> The canonical contract-model inventory is `ARCHITECTURE.md` **Appendix A** (e.g. `InterpretationSession`, `InterpretationTurn`, `TranscriptSegment`, `LatencyEvent`, `CostEstimate`, `SessionSummary`/`ModeSummary`, `ProviderError`, the three provider interfaces + event types, and the API DTOs). When a slice first implements one of those models, the orchestrator adds its row here (model → §) so a field change is paired with the matching `ARCHITECTURE.md` edit in the same round.

## Module organization

```
server/AiInterpreter.Api/
  Controllers/        SessionsController, RealtimeController, CascadeController, EvaluationController, ConfigController
  Realtime/           RealtimeClientSecretService + RealtimeOptions
  Cascade/            CascadeStreamingOrchestrator, CascadeOrchestrator (blob fallback), CascadeWebSocketEndpoint, CascadeModels
  Providers/
    Abstractions/     ISttProvider, ITranslationProvider, ITtsProvider, ProviderEvents, ProviderErrors, AudioFrame
    Deepgram/  OpenAI/  Fakes/
  Sessions/           SessionStore, SessionPersistenceWriter, SessionSummaryService, SessionModels
  Metrics/  Cost/  Evaluation/  Config/  Security/  Common/
server/AiInterpreter.Tests/   xUnit project (see ARCH-020 priority tiers)
```
(Full scaffold: `ARCHITECTURE.md` ARCH-006.)

Layer dependency direction (top depends on bottom, never reverse):

```
Controllers / WebSocket endpoint
  → Application services (orchestrators, SessionStore, SessionSummaryService, EvaluationService)
    → Provider abstractions (ISttProvider / ITranslationProvider / ITtsProvider)
      → Provider implementations (Deepgram / OpenAI / Fakes)
  → Utilities (Metrics, Cost, Evaluation/WER, Persistence, ErrorSanitizer, Common)
```

**Provider logic never lives in controllers** (ARCH-008). Cross-cutting layers (Common, Metrics) may be imported from anywhere. Pin the rule with a structural test where practical — the test *is* the spec for the rule.

## Subagents

See `.claude/agents/README.md` for the canonical inventory + integration points.

<!-- ▼ EXAMPLE BLOCK: area-specific subagent candidates — list candidates that would earn their keep specifically in this area (e.g. an ABI/types syncer for a frontend area, a Pyth/feed verifier for a contracts area). Build only on real friction. ▲ -->

## Lessons logged from prior sessions

The full prose for each lesson lives in `server/LESSONS.md`. This index is the compact orientation surface.

**Lesson numbers are stable IDs** — once assigned, they don't change. New lessons get the next sequential number. `/session-end` proposes additions when it detects them; the user approves before the entry is written and a row is added here.

Lessons start at §1.

| # | Date | Topic | Rule (one-liner) |
|--:|---|---|---|
| 1 | 2026-05-28 | [IOptions config-binding pattern](LESSONS.md#1) | Options are bindable types (class + get/set, or record w/ init + parameterless ctor), NOT ARCH-005 immutable records; inline defaults = single source of truth; expose `const string SectionName`; bind via `Bind(new T())` not `Get<T>()`; non-web test projects need explicit `Microsoft.Extensions.Configuration.*` refs. |
| 2 | 2026-05-28 | [Shared JSON contract + record-`==` gotcha](LESSONS.md#2) | One shared `JsonSerializerOptions` (`Common/JsonDefaults`: camelCase + enum-as-camelCase-string + explicit-null) is the single source for API + persisted JSON; record `==` is reference-based over `List<>`/`Dictionary<>` members, so round-trip tests assert JSON-string equality, not record `==`. |
| 3 | 2026-05-28 | [Degrade-don't-crash on optional config](LESSONS.md#3) | Load optional external config (e.g. `pricing.json`) with a missing-file guard + size guard (pre-read, no OOM) + a *filtered* catch (never bare; never swallow OOM/`SecurityException`) + null-result→`Failure`, returning `Result<T>` so the caller degrades to "unavailable"; deserialize via the shared `JsonDefaults`. |
| 4 | 2026-05-28 | [Host config: env→section bridge + single JSON point](LESSONS.md#4) | Map flat ARCH-028 env vars → Options sections in one `Program.cs` bridge (set only present keys so inline defaults stand; a shared key can fan out); wire HTTP JSON through the single `JsonDefaults.Apply`; host tests touching process env vars set-before-factory + finally-unset + run serialized. |
| 5 | 2026-05-28 | [Error-mapper boundary pattern](LESSONS.md#5) | One static mapper owns the exception→`ProviderError` table; `SafeMessage` is a fixed string per code and **never echoes `ex.Message`/StackTrace** (no secret/stack leaks; B.8 is the full sanitizer); validate the closed `stage` set by caller-contract, not a runtime throw inside the mapper; raise non-exception outcomes (empty/timeout) via explicit factories. |
| 6 | 2026-05-28 | [Streaming-fake pattern](LESSONS.md#6) | Streaming fakes/providers = `async IAsyncEnumerable<TEvent>` + `[EnumeratorCancellation]`; route per-event delay through one `PaceAsync(delay, ct)` (`ThrowIfCancellationRequested` + `await Task.Delay`) for deterministic OCE + no CS1998; error variants `yield` a real-code `*Failed` then `yield break`; test ordering + cancellation, not wall-clock. |
| 7 | 2026-05-28 | [Metrics aggregation: absolute-Timestamp math](LESSONS.md#7) | Aggregate from absolute `Timestamp` (cross-clock safe); `relativeMs` is per-event display (factory clamps ≥0), never a cross-event math input; the aggregator does NOT clamp cross-clock pairs (skew disclosed); missing endpoint → `null`/n·a, never throw. Harness pins: wrong-`RelativeMs` sentinel proves Timestamp-math; literal wire-strings vs `LatencyEventNames` constants catch a typo. |
| 8 | 2026-05-28 | [Cascade orchestrator streaming pattern](LESSONS.md#8) | Nested per-segment loop (each `SttFinal` → its own streamed translation→TTS, sequential; concurrent interleaving deferred ARCH-025); stamp each `first_*` once on real first arrival; manual `MoveNextAsync` enumeration with try/catch only around the move (yield outside); STT timeout = per-event idle timer via arm/disarm `CancelAfter` (a fired CTS can't un-cancel); OCE filter `when (!ct.IsCancellationRequested)` splits caller-cancel from stage-timeout; emit a flat transport-agnostic `IAsyncEnumerable<CascadeOutputEvent>`. |
| 9 | 2026-05-28 | [Cost estimation: branch-on-basis + degrade](LESSONS.md#9) | Branch on `PricingBasis` (not provider); degrade missing config/usage via `Result<CostEstimate>` (lesson §3), but a `0.0` configured rate estimates to `0.0` (distinguish by `decimal?` null, not `==0`); estimate-only factors are named constants surfaced in `Assumptions` + build-confirm-flagged; `decimal`, no rounding; cascade aggregates to one composite estimate keyed by translation model. |
| 10 | 2026-05-28 | [WER: normalize, DP-backtrace S/I/D, unbounded](LESSONS.md#10) | Normalize = invariant-lowercase → strip `\p{P}` → collapse whitespace, accents preserved, punctuation removed (not spaced); DP backtrace attributes S/I/D with documented tie-break (match>sub>del>ins); WER unbounded (never clamp >1.0); empty reference is a precondition (`ArgumentException`, never ÷0); phrase store degrades via lesson §3 behind a DI facade; F.1 boundary must cap hypothesis length (DP-matrix DoS) + never surface `LoadError`. |

<!-- Starts empty. Each row links to its `LESSONS.md` anchor. -->

<!-- Slash commands: see root CLAUDE.md "Slash commands available." Implementer pair: /session-start + /session-end. -->
