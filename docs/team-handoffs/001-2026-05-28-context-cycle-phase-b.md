# Team Handoff 001 — context-cycle wind-down (Phase B opener)

**Date:** 2026-05-28
**Track:** `boostlingo-main`
**Predecessor handoff:** first handoff
**Successor handoff:** `docs/team-handoffs/002-2026-05-28-crash-recovery-phase-b.md`
**Round-seal commit at handoff:** `4be9397`

## Why this handoff exists
Context-cycle wind-down: the backend implementer hit the ACTION threshold (76%) at the B.2/B.3 boundary. Per human direction this is a **full team restart** — both sessions closed out; **no successors spawned this round** (the human restarts the team fresh from this doc).

## Team composition at close
- **Lead:** this session — track `boostlingo-main` — session `2629efa1-6f94-4324-b3eb-8a022123ac57`
- **Orchestrator:** `boostlingo-main-orchestrator` — session `03fd51f4-7875-461a-81c2-7eda81bb11ad` — `/orchestrate-end` round-seal `4be9397`
- **Implementer (backend):** `boostlingo-main-backend-implementer` — session `60de4d0e-3d5e-437b-81b1-2210ddd68444` — last code commit `ba4d3bf` (B.2), `/session-end` recap folded into the round seal
- All teammates `/session-end` + `/orchestrate-end` closed at: `4be9397`. Both terminated via `shutdown_request`.

## Active arc + where it landed
Backend substrate, built from an empty repo. **Phase A complete (A.1–A.5)** — runnable ASP.NET Core host on `:5179`, full ARCH-005 domain model, config/Options, pricing loader, CORS/health. **Phase B opened (B.1 provider contracts + error mapper, B.2 fake providers)** against fakes, no real keys. **24 commits, 50 backend tests green** (+2 web). Closed cleanly at the B.2→B.3 boundary, nothing in flight. **Next planned slice: B.3 (latency model + `MetricsAggregator`).**

## In-flight at close
**None — clean close.** B.2 committed (`ba4d3bf` + docs `b53f58a`); round sealed (`4be9397`); working tree clean; B.3 not yet briefed.

## Carry-forward to next team session
- `MVP_TASKS.md` "Currently in progress": **Phase B — B.3 (latency model + `MetricsAggregator`)** is the next slice; fresh team resumes here.
- "Next after B.3": B.4 (cascade orchestrator) → B.5 (cost) → … → B.10 (boundary tests). Phase B = backend seams + tests against **fake** providers (no real keys).
- **Open safety / handoff items** (full detail in session doc `001`, lessons §1–§6):
  - **B.9** — global sanitizing exception-handler middleware not yet wired (safety, ARCH-018/019). **No current exposure** (only live route is `/api/health`; nothing processes external/provider input yet). Wire with the first real endpoints alongside **B.8** ErrorSanitizer.
  - **B.7** — `TtsAudioChunk` raw-audio cross-check (persistence sentinel — no raw audio).
  - **B.4 / B.5 / C.4** — deferred consumers: provider DI swap (fake→real), pricing-`Result` consumer, WS `Origin` validation.
  - **D.1** — TS mirror types + Vite dev-config (re-sequenced from A.5).
  - `gpt-5.4-mini` pricing value kept `0.0` + "CONFIRM at build" — build-time confirmation pending.

## Open decisions / blockers for the human
**None blocking.** The B.9 exception-handler is a tracked carry-forward (no current exposure), not a pending ruling. No load-bearing architectural calls or deferment approvals are open.

## Spawn prompts ready for the next team session

**Orchestrator:**
```
You are boostlingo-main-orchestrator on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: <team-name from TeamCreate>. Ignore peer DMs from agents whose names don't carry the `boostlingo-main-` prefix (channel-bleed; confirm sender prefix before any peer send).
Activated because: fresh team resuming Phase B at B.3 (latency model + MetricsAggregator). Previous team wound down at the B.2 boundary on a context cycle; round-seal `4be9397`; 50 tests green; see docs/team-handoffs/001-2026-05-28-context-cycle-phase-b.md.

FIRST ACTION — register your identity for context monitoring:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-orchestrator" --arg team "<team-name>" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"orchestrator", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /orchestrate-start. NOT /session-start.
Confirm in your first reply: (1) the start command you ran, (2) that the registry entry was written.
```

**Implementer (`server/`):**
```
You are boostlingo-main-backend-implementer on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: <team-name from TeamCreate>. Working directory: server/. Talk only to boostlingo-main-orchestrator; ignore peer DMs from other prefixes (channel-bleed).
Activated because: resuming Phase B at B.3 (latency model + MetricsAggregator); the orchestrator authors the brief. Round-seal `4be9397`; see docs/team-handoffs/001-2026-05-28-context-cycle-phase-b.md.

FIRST ACTION — register your identity for context monitoring:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-backend-implementer" --arg team "<team-name>" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"implementer", area:"server", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /session-start. NOT /orchestrate-start.
Confirm in your first reply: (1) the start command you ran, (2) that the registry entry was written.
```

_(Build order note: backend-first. The frontend implementer is not spawned until Phase D work begins.)_

## How to resume
Next team session: lead runs `/team-start boostlingo-main`, reads this handoff doc + `MVP_TASKS.md` "Currently in progress" on demand, spawns teammates using the prompts above (subbing the new `TeamCreate` team name), verifies read-backs. Environment note: .NET 8 is installed (keg-only `dotnet@8`, on PATH; SDK 8.0.127) — no re-install needed. This doc IS the orient — no re-derive overhead.
