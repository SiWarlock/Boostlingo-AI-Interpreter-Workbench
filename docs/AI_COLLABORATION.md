# How this was built with AI

> Phase G.2 deliverable — how the AI agent(s) were directed to build the AI Interpreter Workbench. The operational conventions that *enforce* this (constraints, comm rules, commit discipline) live in the repo's `CLAUDE.md` / `AGENTS.md` (root + `server/` + `web/`); this doc is the narrative.

## Architecture-first, not code-first

The build never started from code. The pipeline was:

1. **PRD → architecture contract.** The PRD was turned into a binding **`ARCHITECTURE.md`** (anchored `ARCH-###` sections: the domain model, provider interfaces, the API contract, the metrics/cost/WER/persistence models, the security boundaries). This is the single source of truth — every later decision cites an anchor.
2. **Architecture → task plan.** `ARCHITECTURE.md` was decomposed into **`MVP_TASKS.md`** — a phase plan (A→G) where every task references the `ARCH-###` anchors it implements and carries explicit happy/edge/error test scenarios. Build order is deliberate: backend seams + tests first, then real providers, then the frontend, then the two highest-risk integrations (Realtime WebRTC, live-streaming cascade) last, each with a documented fallback.
3. **Task → TDD slice.** Each task became a `/tdd` slice driven from a written **brief** (`docs/briefs/NNN-*.md`) — a permanent design-decision audit trail. The brief pre-loads the design questions; the implementer answers them at a review checkpoint *before* writing the implementation.

The effect: the agent is steered by a contract, not by vibes. When implementation surfaced a detail the architecture didn't cover, that was a flagged cross-doc event — the architecture was updated atomically, not silently drifted around.

## The agent-team pattern

Work ran as a small Claude agent team with separated roles:

- An **orchestrator** owns planning, scoping, brief authoring, the design-review checkpoint, doc/architecture upkeep, and commits.
- One **implementer per code area** (`server/`, `web/`) runs the TDD cycles.
- A thin **team lead** is the human interface + escalation conduit.

Communication is *bounded* — a fixed set of checkpoints per slice (design review, ship/no-ship, done) — so parallel agents stay deterministic and the round narrative stays readable. Decisions that touch a safety invariant, cut scope, or shape a load-bearing contract escalate to the human; everything else the agents settle directly.

## TDD discipline

Deterministic code is **test-first**: a failing test pins the behavior before the implementation exists (RED → design-review → GREEN → refactor → all-green). A mid-cycle **review checkpoint** lets the orchestrator catch missing boundary tests before any implementation is written — repeatedly catching, e.g., a swapped-argument bug or a silently-truncated value that the happy-path tests would have missed. Fresh-eyes **code-quality** and **security** reviews run on each slice's touched files before it ships.

Non-deterministic work (browser mic/WebRTC/playback internals, real-provider network calls, LLM output *quality*) is explicitly exempt from unit TDD and covered instead by manual smoke + a demo checklist — the line is drawn at "can a failing test pin this deterministically?"

## The constraints the agent was held to

These are hard invariants, enforced by tests, not aspirations:

- **No standard provider keys in the frontend.** Keys are backend-only; the browser receives only the short-lived ephemeral Realtime credential (`ek_…`), which is never persisted.
- **No raw audio persisted.** Session JSON holds transcripts / metrics / errors only — pinned by an active sentinel scan.
- **Provider errors are sanitized** before reaching the UI — no stack traces, no secrets, no raw payloads.
- **Provider interfaces are preserved** — swapping a provider (or adding a second one per stage) is a contained change behind `ISttProvider` / `ITranslationProvider` / `ITtsProvider`; provider logic never leaks into controllers.
- **Streaming is honest** — stage latencies are stamped on real provider-event arrival, never synthesized; the translation stage genuinely streams.
- **Scoped commits** — each slice stages its own files explicitly (never `git add -A`); safety-invariant slices get their own commit; orchestrator-owned docs (architecture, task tracker, lessons) commit separately from implementation.

## Accumulated learning

Each slice's non-obvious gotchas are banked as numbered **lessons** (`server/LESSONS.md`, `web/LESSONS.md`) with a compact index in each area's conventions file — so a later slice benefits immediately instead of re-discovering the same trap. The lessons are stable IDs, never reordered.

## Where to look

- **`ARCHITECTURE.md`** — the binding design contract (anchor index at the top).
- **`MVP_TASKS.md`** — the phase plan + build log + carry-forward working set.
- **`docs/briefs/`** — the per-slice design-decision audit trail.
- **`docs/sessions/`** — the chronological session record (what landed each round).
- **`{server,web}/LESSONS.md`** — the banked engineering lessons.
- **`CLAUDE.md` / `AGENTS.md`** (root + areas) — the operational conventions the above narrative is enforced by.
