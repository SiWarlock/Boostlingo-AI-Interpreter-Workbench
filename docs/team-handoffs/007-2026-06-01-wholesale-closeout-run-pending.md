# Team Handoff 007 — Wholesale Close-Out (live soak run PENDING)

**Date:** 2026-06-01
**Track:** `boostlingo-main`
**Predecessor handoff:** `006-2026-06-01-lead-compaction-preharness.md`
**Successor handoff:** `008-2026-06-01-eod-live-run-g5-findings-sealed.md`
**Round-seal commit at handoff:** `90acf5f` (orch `/orchestrate-end` seal) · lead config commit `bd10933` (HEAD)

## Why this handoff exists
Wholesale team close-out — user's **laptop has a hardware issue and must restart**. The whole team (orch + FE + BE) closed out cleanly so nothing is lost; all three re-spawn FRESH post-restart. This is a clean pause, NOT an arc completion.

## ⭐ THE ONE LOAD-BEARING STATE: the live soak run is PENDING — it never ran
**This is NOT a failure.** The G.4 soak harness is **code-complete, sealed (`ebaede9`), and run-ready** at `?soak=1`. The live 5-minute × both-modes run simply never got to execute — driving it via browser automation was **blocked on `claude-in-chrome` permission** (the lead's `navigate` was permission-denied even after the user said "go"; it's a tool-permission-mode gate, not a chat approval).

**On resume, the live run is the IMMEDIATE next action.** Two ways to drive it:
- **(a) Lead drives it** — once the `claude-in-chrome` browser tool is approved (approve the tool prompt, or run in a permissive mode). Flow: navigate to `http://localhost:5173/?soak=1` → `SoakPanel` → select mode → "Run soak" (~5 min) → capture the rendered `SoakReport` JSON. Run **cascade** then **realtime**, one at a time (the panel resets the report each run).
- **(b) User drives it manually** — open `http://localhost:5173/?soak=1` (hard-refresh), run cascade then realtime (~5 min each), paste the two `SoakReport` JSON dumps; the lead/orch interpret.

Then: **CF76** live cost-wire-shape check (does the live GA `response.done.usage` `output_token_details.audio_tokens` field-path match the FE `extractRealtimeUsage` parser — small FE tweak only if it differs; else realtime cost is already correct per 053-C2b) → the two `SoakReport`s feed the **G.5 write-up** (`docs/COMPARISON_WRITEUP.md`, currently `[SMOKE:…]` placeholders).

**Full detail:** orch handoff `docs/sessions/027-2026-06-01-orch-handoff-wholesale-closeout-run-pending.md` + the MVP_TASKS "Currently in progress" banner.

## Team composition at close (all sids DIE on restart — re-spawn fresh)
- **Lead:** this session, track `boostlingo-main`, sid `27cb24fd`.
- **Orchestrator:** `boostlingo-main-orchestrator`, sid `24514e33` — `/orchestrate-start` + `/orchestrate-end` closed; round seal `90acf5f` (landed the owed G.4 086/090/092 cross-doc rows + tracker reconcile); handoff doc `027`.
- **Frontend implementer:** `boostlingo-main-frontend-implementer`, sid `c7738b46` — `/session-start` + `/session-end` (clean no-op: fresh respawn, no work, nothing to commit).
- **Backend implementer:** `boostlingo-main-backend-implementer`, sid `71e7346d` — `/session-end`, session doc `026` (`5c681d2`), backend 488 green.
- The lead committed the deferred `.claude/commands/tdd.md` reviewer-disable edit at this `/team-end`: **`bd10933`** (the orch's auto-mode classifier correctly blocked it from self-committing its own reviewer-dispatch config off a relayed directive).

## Active arc + where it landed
The team built **G.4 — the 5-minute synthetic soak-harness** (consumes the Phase-J bidirectional capability). **8 slices, all sealed (`ebaede9`), server 488 / web 394 green:** 086 dev `/api/dev/tts` · 087 deterministic core · 088 `getUserMedia`-DI-seam injection · 089a runner · 090 additive `/wer {reference,hypothesis}` (security-reviewer clean) · 089b/091 live composition + `?soak=1` dev entry · 092 realtime output-token store-surface · 093 overlap (decision-2A) + precise disconnect. The live run + **G.5 write-up** are what remain (see the run-pending banner above). Note: brief-092's "realtime cost undercount" premise was **stale** — realtime cost was already priced from reported `response.done.usage` tokens (053-C2b); the output-audio work was overlap-only.

## In-flight at close
**None — clean close.** No mid-slice work. The "live run pending" is a NEXT ACTION (lead/user-driven), not an interrupted slice.

## Carry-forward to next team session
- `MVP_TASKS.md` "Currently in progress": the wholesale-close-out banner → the live run is the immediate next action; then CF76 + G.5.
- Queued (non-urgent): Finding-3 manual WER flow (user coordinates a capture); per-stage cascade cost breakdown (user-undecided); the deferred Phase-J/067–077 ARCH-doc-sync pass (its own focused pass — do NOT cram into G.5).
- After G.5: G.3 demo script; G.6 optional deploy.

## Open decisions / blockers for the human
- **Browser-automation permission** — to let the lead drive the soak run, the `claude-in-chrome` tool needs approval (tool prompt / permissive mode). Otherwise the user drives the `SoakPanel` manually (option b above). **Decide on resume.**
- **No remote configured** — everything is local-only; no push.

## Servers (lead owns; killed by the restart — restart on resume)
- **Backend `:5179`** (keys + `gpt-5-nano` + Debug):
  ```bash
  cd server/AiInterpreter.Api && lsof -ti tcp:5179 | xargs kill -9 2>/dev/null
  set -a && source ../../.env && set +a && export OPENAI_TRANSLATION_MODEL=gpt-5-nano && export Logging__LogLevel__Default=Debug && dotnet run --urls http://localhost:5179
  ```
- **Frontend `:5173`** (Vite): `cd web && lsof -ti tcp:5173 | xargs kill -9 2>/dev/null; npm run dev` — then **hard-refresh (Cmd+Shift+R)**.

## Spawn prompts ready for the next team session

**Orchestrator:**
```
You are boostlingo-main-orchestrator on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Ignore peer DMs from agents whose names don't carry the `boostlingo-main-` prefix (channel-bleed).
Activated because: POST-RESTART resume. The G.4 soak harness is CODE-COMPLETE + sealed (ebaede9). The LIVE 5-min × both-modes soak run is the IMMEDIATE next action — it never ran (was blocked on browser-automation permission). The lead drives the run (or the user drives SoakPanel manually + pastes the 2 SoakReport JSONs); you route any run-Finding into a fix slice + then the G.5 write-up (docs/COMPARISON_WRITEUP.md, [SMOKE] placeholders). Track CF76 (live cost-wire-shape check) without acting pre-emptively.

FIRST ACTION — register your identity for context monitoring:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-orchestrator" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"orchestrator", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /orchestrate-start (NOT /session-start). Then READ docs/team-handoffs/007-2026-06-01-wholesale-closeout-run-pending.md + docs/sessions/027-*.md.
Confirm in your first reply: (1) the start command, (2) registry entry written, (3) one-line read-back of the immediate plan (the live run).
```

**Frontend implementer (`web/`):**
```
You are boostlingo-main-frontend-implementer on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Working directory: web/. Talk only to boostlingo-main-orchestrator; ignore other-prefix DMs.
Activated because: POST-RESTART resume. The G.4 harness is code-complete (web 394 green, HEAD has the seal). Stand by with full headroom for any run-surfaced fix slice (disconnect/drift/leak handling, or the CF76 realtime-cost-wire-shape parser tweak if the live GA response.done.usage audio-token field-path differs) + the G.5 phase. No brief until the run surfaces a Finding.

FIRST ACTION — register your identity:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-frontend-implementer" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"implementer", area:"web", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /session-start (NOT /orchestrate-start) in web/. Confirm: (1) start command, (2) registry written. Then stand by for the orchestrator.
```

**Backend implementer (`server/`):** _(spawn only if a run-Finding needs backend/WS work; otherwise the team can run lead + orch + FE until then)_
```
You are boostlingo-main-backend-implementer on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Working directory: server/. Talk only to boostlingo-main-orchestrator; ignore other-prefix DMs.
Activated because: POST-RESTART resume. Backend is complete (488 green, last work 090 `6b83ac6`). On hold — available if a run-Finding needs WS/continuous-stability or cascade work.

FIRST ACTION — register your identity:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-backend-implementer" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"implementer", area:"server", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /session-start (NOT /orchestrate-start) in server/. Confirm: (1) start command, (2) registry written. Then stand by.
```

## How to resume
Post-restart: restart both servers (commands above), then lead runs `/team-start boostlingo-main`, reads this handoff + handoff `027` + `MVP_TASKS.md` "Currently in progress" on demand, spawns teammates with the prompts above, verifies read-backs. **First action after the team is up: drive the live soak run** (resolve the browser-permission decision first). Standing directives still in force: **TIMEBOX OFF** (`build-timebox`); per-slice reviewers DISABLED (`tdd.md`, now committed `bd10933`); verify-dead-before-respawn (`orch-cycle-naming-collision`); lead self-cycle 80–85%; `command git commit` + verify HEAD (`rtk-git-commit-swallow`); hard-refresh after a Vite restart (`vite-restart-hard-refresh`).
