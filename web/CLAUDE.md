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
npm run lint && npm run typecheck && npm run test
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

| Model | `ARCHITECTURE.md` section | Notes |
|---|---|---|
| _(none yet)_ | — | Populate as typed models land in `web/src/types/`. |

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

<!-- Starts empty. Each row links to its `LESSONS.md` anchor. -->

<!-- Slash commands: see root CLAUDE.md "Slash commands available." Implementer pair: /session-start + /session-end. -->
