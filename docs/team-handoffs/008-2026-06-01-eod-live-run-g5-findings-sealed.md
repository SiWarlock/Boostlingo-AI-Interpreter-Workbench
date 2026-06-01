# Team Handoff 008 — EOD: live run + G.5 + 3 live-test Findings sealed

**Date:** 2026-06-01
**Track:** `boostlingo-main`
**Predecessor handoff:** `007-2026-06-01-wholesale-closeout-run-pending.md`
**Successor handoff:** _(filled in when the next /team-end runs)_
**Round-seal commit at handoff:** `bc9536c` (round seal) → `41c52c9` (soak evidence, final HEAD)

## Why this handoff exists
End-of-day wrap (user-directed). The whole post-restart arc landed cleanly — the live soak run that was pending in handoff 007 ran, fed G.5 + G.3, and the three live-test Findings the user surfaced were all fixed and confirmed. Clean pause, NOT arc-incomplete.

## What landed this round (the arc 007 left pending → done)
- **Live real-key soak run** (lead-driven, both modes ×5 min): all 3 ARCH-020 stability booleans green both modes; reports saved + committed under `docs/soak-runs/` (`41c52c9`).
- **Cost-accuracy fix (094/095)** `2ac8adb`+`5237820` — realtime cached-audio was over-counted ~1.5× (cached billed at full rate); corrected → realtime ~$0.24/min. Live-verified via a post-fix re-run (1.52× ratio matched the prediction). **CF76 closed as a no-op** (the parser already read the right field path).
- **G.5 comparison write-up COMPLETE** (`docs/COMPARISON_WRITEUP.md`, all `[SMOKE]`/`[PENDING]` placeholders filled with measured numbers): realtime ~$0.24/min (climbs with conversation context) vs cascade $0.012/min (flat) ≈ **~20×**; ~669 ms vs ~1914 ms speech→first-audio (~2.9× faster realtime); WER 0.025 vs 0.174 (synthetic-audio caveat); honest-degrade disclosures preserved.
- **G.3 demo script** (`docs/DEMO_SCRIPT.md`) — a ~6-min presenter-ready SAY/SHOW spoken script. **KEPT LOCAL-ONLY** per user (untracked; `.gitignore`'d via `git rm --cached` at the seal). The revised content is on disk, uncommitted.
- **096 push-to-talk WER** `ab93fbd` — Finding 3 closed (the eval recording is now user-controlled with a countdown + visible state; empty capture → honest "no speech — n/a", never a fabricated 100%).
- **3 live-test Findings (A/B/C) — ALL CLOSED**, each diagnosed against the user's REAL session JSON (lesson §34):
  - **A** `8b2e555` (097) — realtime cost wasn't displaying; `IsEmptySilence` was excluding every realtime turn from the summary. Fixed (cost-bearing discriminator). NOT a 094/095 regression.
  - **B** `8d6b25e` (098) — realtime interpreter ANSWERED questions instead of translating. Restructured the mint instruction templates (conduit framing + target-language lock + translate-the-question). **USER-LIVE-CONFIRMED** translates-not-answers (eval-observed; the deterministic deliverable is the string + presence test).
  - **C** `692fea4` (099) — cascade per-turn cards flipped to N/A on a trailing empty auto-VAD turn; shared `selectDisplayTurn` now skips trailing empty-silence (FE sibling of §39/097). Backend was correct.
- **Local-dev runbook** `docs/runbooks/local-dev-and-test-runbook.md` (committed) — lead-authored at user request; how to start (:5179/:5173) + smoke + test.
- Lessons: server §40/§41 (+ §39 refinement), web §36/§37/§38 (+ §20 annotation). Session docs 028 (BE) / 029 (FE) / 030 (orch).

**Final suites:** server 494 / web 406 green. **Final HEAD:** `41c52c9` (local-only, no remote/push).

## Team composition at close (all sids die on next machine restart — re-spawn fresh)
- **Lead:** this session, track `boostlingo-main`.
- **Orchestrator:** `boostlingo-main-orchestrator` — `/orchestrate-end` closed; round seal `bc9536c` + soak-evidence commit `41c52c9`; orch session doc 030.
- **Frontend implementer:** `boostlingo-main-frontend-implementer` — `/session-end`, doc 029 (`20bb057`).
- **Backend implementer:** `boostlingo-main-backend-implementer` — `/session-end`, doc 028 (`8a692fb`).

## In-flight at close
**None — clean close.** No mid-slice work; all 6 slices (094–099) sealed; G.5/G.3 done; all 3 Findings closed.

## Carry-forward to next team session
- `MVP_TASKS.md` "Currently in progress": the 2026-06-01 close-out banner (this round's state).
- **Next-resume candidates (no urgent/queued slice):**
  - Deferred **Phase-J / 067–077 ARCH-doc-sync pass** (docs-only, its own focused pass — do NOT cram elsewhere).
  - **Per-stage cascade cost breakdown** (user-undecided — needs a scoping decision to action).
  - **G.6 optional deploy** (HITL).
- **3 new Carry-forward hardening follow-ups (added this round, none urgent):**
  1. **Realtime transcript persistence** — realtime turns persist 0 transcripts (FE-store-side, never sent on `/complete`) → history drill-in shows no transcripts for realtime sessions. By-design today; revisit as a wire-path decision.
  2. **Soak WER-alignment fragility** — a stray leading utterance shifts the ref↔hyp pairing by one → spurious ~100% WER in the soak (artifact, not a quality regression). Harness-robustness item.
  3. **Cascade interpreter-prompt hardening** — 098's forbid-answering restructure was realtime-only; the cascade `gpt-5-nano` translation prompt may warrant the same hardening (not yet user-observed in cascade).

## Open decisions / blockers for the human
- **Per-stage cascade cost breakdown** — needs a user decision before it's a buildable slice.
- **Demo script is local-only** (`docs/DEMO_SCRIPT.md` untracked, intentional) — edit/regenerate locally; it won't be in the repo.
- **No remote configured** — everything local-only; no push.
- Servers (lead-owned) may still be running — backend `:5179` (098 build), frontend `:5173`. Kill with `lsof -ti tcp:5179 | xargs kill -9` / `lsof -ti tcp:5173 | xargs kill -9` if pausing the machine.

## Spawn prompts ready for the next team session

**Orchestrator:**
```
You are boostlingo-main-orchestrator on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Ignore peer DMs from agents whose names don't carry the `boostlingo-main-` prefix (channel-bleed).
Activated because: resuming after EOD pause (handoff 008). The live-run + G.5 + 3-Findings arc is COMPLETE & sealed (HEAD 41c52c9). No urgent queued slice — next candidates: the deferred Phase-J/067–077 ARCH-doc-sync pass, the per-stage cascade cost breakdown (needs a user decision), G.6 optional deploy, or the 3 Carry-forward hardening follow-ups. Await the lead's dispatch for which.

FIRST ACTION — register your identity for context monitoring:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-orchestrator" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"orchestrator", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /orchestrate-start (NOT /session-start). Then READ docs/team-handoffs/008-2026-06-01-eod-live-run-g5-findings-sealed.md.
Confirm: (1) start command, (2) registry written, (3) one-line read-back of the resume state.
```

**Frontend implementer (`web/`):**
```
You are boostlingo-main-frontend-implementer on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Working directory: web/. Talk only to boostlingo-main-orchestrator; ignore other-prefix DMs.
Activated because: resuming after EOD pause (handoff 008). web 406 green at HEAD. Stand by for the orchestrator's brief (no queued FE slice yet — depends on the lead's next dispatch).

FIRST ACTION — register your identity:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-frontend-implementer" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"implementer", area:"web", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /session-start (NOT /orchestrate-start) in web/. Confirm: (1) start command, (2) registry written. Then stand by.
```

**Backend implementer (`server/`):** _(spawn only if the next dispatch needs backend work)_
```
You are boostlingo-main-backend-implementer on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Working directory: server/. Talk only to boostlingo-main-orchestrator; ignore other-prefix DMs.
Activated because: resuming after EOD pause (handoff 008). server 494 green at HEAD. On hold — spawn only if the next dispatch needs WS/cascade/cost backend work (e.g. cascade prompt hardening, per-stage cost).

FIRST ACTION — register your identity:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-backend-implementer" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"implementer", area:"server", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /session-start (NOT /orchestrate-start) in server/. Confirm: (1) start command, (2) registry written. Then stand by.
```

## How to resume
Next team session: lead runs `/team-start boostlingo-main`, reads this handoff doc + `MVP_TASKS.md` "Currently in progress" on demand, spawns teammates with the prompts above, verifies read-backs. Restart the servers if needed (runbook: `docs/runbooks/local-dev-and-test-runbook.md`). **Standing directives still in force:** TIMEBOX OFF (`build-timebox`); per-slice reviewers DISABLED (`tdd.md`, committed `bd10933`); autonomous operation after initial go (`autonomous-operation`); verify-dead-before-respawn (`orch-cycle-naming-collision`); lead self-cycle 80–85%; `command git commit` + verify HEAD (`rtk-git-commit-swallow`); hard-refresh after a Vite restart (`vite-restart-hard-refresh`).
