# Team Handoff 003 — UI Design Pause (Phase G/H boundary)

**Date:** 2026-05-31
**Track:** `boostlingo-main`
**Predecessor handoff:** `docs/team-handoffs/002-2026-05-28-crash-recovery-phase-b.md`
**Successor handoff:** _(filled in when the next /team-end runs)_
**Round-seal commit at handoff:** `d0a457b`

## Why this handoff exists
User-directed pause at the Phase-G/H boundary so the user can design the UI in **Claude Design** (Phase H is style-first); the team resumes to implement that design, then run the real-key smoke.

## Team composition at close
- **Lead:** this session — track `boostlingo-main`.
- **Orchestrator:** `boostlingo-main-orchestrator` — session `6b40ed8d…` (persisted across Phase F F.1→F.4 + Phase-G docs + Phase-H setup) — `/orchestrate-end`-closed at round-seal `d0a457b`.
- **Implementer (backend):** `boostlingo-main-backend-implementer` — session `63313895…` — `/session-end`-closed after F.4 (session doc 012 `1bb8ed2`). The frontend implementer was already cycled out earlier.
- All teammates closed at round-seal `d0a457b`.

## Active arc + where it landed
**Phases A–F COMPLETE + F.4.** Both interpretation modes (cascade + realtime) ship; full evaluation/comparison layer ships; F.4 eval-turn-exclusion landed so comparison turn counts are exact. **Backend 345 green · web 193 green.** The real-key smoke was set up (servers started, keys loaded) but **paused before any test turn** because the user (correctly) wants the UI styled first — the frontend has zero CSS today (functionally complete, visually raw). Next planned work: **Phase H / H.1** (UI baseline) against the user's Claude Design output.

## In-flight at close
**None — clean.** The smoke is user-driven and not started; H.1 brief `047` is drafted-not-dispatched; no slice mid-`/tdd`.

## ⭐ Critical resume notes (read before starting)
1. **Backend needs the `.env` SOURCED to load keys** — the app has NO `.env` loader (tracked Finding → task **G.2b**: add DotNetEnv or document). Plain `dotnet run` leaves `/api/config` → `configured:false`. Start the backend with:
   ```bash
   cd server/AiInterpreter.Api && set -a && source ../../.env && set +a && dotnet run --urls http://localhost:5179
   ```
   (Frontend: `cd web && npm run dev` → `:5173`. The Vite proxy forwards `/api` → `:5179`, so the frontend is zero-config.)
2. **Resume ORDER (user-chosen "style-first"):** implement **H.1** against the user's design → **then** run the real-key smoke → **then** fill G.5 numbers + author G.3 demo script.
3. **Smoke runbook ready:** `docs/runbooks/real-key-smoke-runbook.md` (full T1–T7 matrix + capture table + safety verify).
4. **Design inputs delivered to the user:** `docs/design/ui-design-spec.md` (component/state/layout spec) + `docs/design/claude-design-prompt.md` (the Claude Design prompt).
5. **Servers may still be running** from the smoke setup (backend `:5179` via the sourced-env command above; frontend `:5173`). A runtime artifact `server/AiInterpreter.Api/log.txt` is untracked (gitignore it; explicit-`git add` discipline keeps it out of commits meanwhile).

## Carry-forward to next team session
- `MVP_TASKS.md` "Currently in progress": **Phase H / H.1** (UI usability+visual baseline; styling slice, MANUAL-SMOKE-EXEMPT; CSS/markup-only, no logic change, suites stay green; clean-separation ARCH-007 holds). Brief `047` drafted.
- "Next after H.1": real-key smoke → G.5 number-fill + G.3 demo script → G.4 stability → H.2+ deeper polish.
- Open Carry-forward (see `MVP_TASKS.md`): G.2b `.env`-loader; the standing real-key smokes; deeper Phase-H polish; (non-blocking) backend `ModeTransitionEvent` persistence.

## Open decisions / blockers for the human
- **Waiting on the user's UI design** (Claude Design output) before H.1 can be implemented — the user will return with screenshots / HTML-CSS / tokens.
- **H.1 CSS approach is open** — orch's default is dependency-free tokens + CSS Modules (no Tailwind); **defer to whatever the Claude Design output emits + the user's preference** (if the design ships Tailwind/specific CSS, align H.1 to it).
- **Real-key smoke still pending the user** (provider keys + a live run + modest spend) — non-blocking until after H.1.
- No load-bearing architectural decisions pending.

## Spawn prompts ready for the next team session

**Orchestrator:**
```
You are boostlingo-main-orchestrator on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Ignore peer DMs without the `boostlingo-main-` prefix (channel-bleed).
Activated because: resuming from handoff 003 (UI design pause). Phases A–F COMPLETE + F.4 (backend 345 / web 193 green); round-seal `d0a457b`. User chose STYLE-FIRST: implement Phase H / H.1 (UI baseline) against the user's Claude Design output, THEN run the real-key smoke, THEN G.5 numbers + G.3 demo. H.1 brief `047` is drafted (styling brief — visual acceptance + manual-smoke, NOT /tdd; CSS/markup-only, suites stay green). Design inputs: docs/design/ui-design-spec.md + docs/design/claude-design-prompt.md. The user will provide the actual design; finalize 047 against it. Resume notes in docs/team-handoffs/003-2026-05-31-ui-design-pause.md.

FIRST ACTION — register your identity:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-orchestrator" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"orchestrator", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /orchestrate-start. NOT /session-start.
Confirm in your first reply: (1) the start command you ran, (2) the registry entry was written.
```

**Implementer (`web/` frontend):**
```
You are boostlingo-main-frontend-implementer on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Working directory: web/ (React + Vite + TS). Talk only to boostlingo-main-orchestrator; ignore other-prefix DMs (channel-bleed).
Activated because: resuming from handoff 003 — implementing Phase H / H.1 (UI usability+visual baseline) against the user's Claude Design output. The frontend is functionally complete (web 193 green) but has ZERO CSS — H.1 is a STYLING slice: a design-system/CSS baseline + dashboard layout + visible active-mode selection + session/turn status cues + styled controls/panels, CSS/markup-ONLY (no logic change; clean-separation ARCH-007 holds; suites stay green). Visual styling is MANUAL-SMOKE-EXEMPT (not /tdd). The orchestrator dispatches the finalized H.1 brief (047). Context: docs/design/ui-design-spec.md (component/state/layout) + the user's design.

FIRST ACTION — register your identity (from web/):
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-frontend-implementer" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"implementer", area:"frontend", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /session-start. NOT /orchestrate-start.
Confirm in your first reply: (1) the start command you ran, (2) the registry entry was written, (3) your working directory is web/.
```

## How to resume
When the user returns with the UI design: lead runs `/team-start boostlingo-main`, reads this handoff + `MVP_TASKS.md` "Currently in progress", spawns the orchestrator + a `web/` frontend implementer using the prompts above, verifies read-backs, and hands the user's design to the orchestrator to finalize brief `047`. This doc IS the orient — no re-derive overhead.
