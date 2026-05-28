---
description: Team lead — close-out the team session; write handoff doc; surface what's next (user-on-demand OR auto-cycle when lead's own context hits threshold).
allowed-tools: Read, Edit, Write, Bash, AskUserQuestion
argument-hint: "<short topic>"
---

> **Role guard — TEAM LEAD only.** `/team-end` is the lead's pause-the-team close-out. Not the same as `/orchestrate-end` (orchestrator's round close-out) or `/session-end` (implementer's session close-out). Those run per-session; this runs when the team is **fully pausing** (end of day, arc-complete, lead-cycle, mode-swap to solo).

Argument: `$ARGUMENTS` — short topic for the handoff doc filename (e.g. `eod-YYYY-MM-DD`, `arc-X-complete`).

**When to invoke:**
- **End of day / weekend** — preserving coordination state so tomorrow's `/team-start` resumes cleanly.
- **Arc complete** — a major milestone landed (phase done, demo green, deploy successful); want a clean handoff doc.
- **Lead context approaching limit** — the lead itself needs to cycle (rare but possible).
- **Solo-mode swap** — dropping the team to continue solo, or formal pause before swap.

**When NOT to invoke:**
- Per slice / per task / per phase / per round — the close-out gate is `/session-end` + `/orchestrate-end`, not this.
- Mid-arc — `/team-end` is for **pausing**, not for natural work boundaries.

## Step 0 — Confirm user-explicit go

`/team-end` runs **only on user-explicit go**, just like `/session-end` and `/orchestrate-end`. If you reached this command without the user signaling it, stop and surface the question instead — don't auto-end the team at a natural boundary.

## Step 1 — Gate: all teammates at closed state

Before writing anything:

1. **Every implementer must be `/session-end`-closed.** Confirm by checking for an in-flight slice — if any implementer is mid-`/tdd`, mid-Step-2.5, or mid-anything, **STOP**. Surface to the user; ask whether to (a) wait for the slice to land + `/session-end`, or (b) abort the close-out.
2. **The orchestrator must be `/orchestrate-end`-closed** for the round. Check `git log --oneline -1` — the most recent commit should be the round terminal commit (typically `docs(tasks): ...`). If a slice commit is the tip, the round isn't sealed — STOP + surface.

Do NOT proceed to Step 2 until both gates pass. **Never tear down mid-work.**

## Step 2 — Read current state pointers

Read for the handoff doc:
1. `git log --oneline -5` — last 5 commits, includes the round-seal hash.
2. `MVP_TASKS.md` "Currently in progress" + "Next session target" + "Carry-forward to upcoming briefs" + last Log entry.
3. The most recent `docs/sessions/<NNN>-*.md` — what just landed.
You already hold the rest of the coordination state in memory (team composition, active arc, open decisions, who's been working on what — derived from per-slice context-check pings + escalations during the team's life).

## Step 3 — Compute the handoff doc number

```bash
ls docs/team-handoffs/ 2>/dev/null | head -20
```

Take the highest numeric prefix + 1, zero-pad to 3 digits. If the `docs/team-handoffs/` directory doesn't exist yet, create it + start at `001`. Filename: `<NNN>-<YYYY-MM-DD>-<topic>.md` per `$ARGUMENTS`.

## Step 4 — Write the handoff doc

```markdown
# Team Handoff <NNN> — <topic>

**Date:** YYYY-MM-DD
**Track:** <track from /team-start, or "solo">
**Predecessor handoff:** <docs/team-handoffs/NNN-1-...md if any, else "first handoff">
**Successor handoff:** _(filled in when the next /team-end runs)_
**Round-seal commit at handoff:** `<commit hash from git log>`

## Why this handoff exists
<one sentence: end-of-day / arc-complete / lead-cycle / mode-swap>

## Team composition at close
- Lead: this session (track `<track>`)
- Orchestrator: `<track>-<area>-orchestrator` — last session ID + last commit
- Implementer(s): `<track>-<area>-implementer` for each spawned area — last session ID + last commit
- All teammates `/session-end` + `/orchestrate-end` closed at: <round-seal commit>

## Active arc + where it landed
<2-3 sentences: what arc the team was on, what landed in the closing round, what's the next planned slice>

## In-flight at close (should be empty)
<list anything started-but-not-closed, OR "None — clean close">

## Carry-forward to next team session
- `MVP_TASKS.md` "Currently in progress": <quote>
- `MVP_TASKS.md` "Next session target": <quote>
- Open Carry-forward items: <bullet list, or pointer to MVP_TASKS.md section>

## Open decisions / blockers for the human
<bullet list — load-bearing architectural calls pending, deferment approvals pending, deploy/env questions, etc. Empty if none.>

## Spawn prompts ready for the next team session
**Orchestrator:**
```
<filled-in template from /team-start with the right track + activated-because line>
```

**Implementer (`<area>`):**
```
<filled-in template from /team-start with the right track + activated-because line>
```

## How to resume
Next team session: lead runs `/team-start <track>`, reads this handoff doc + `MVP_TASKS.md` "Currently in progress" on demand, spawns teammates using the prompts above, verifies read-backs. No re-orient overhead — this doc IS the orient.
```

## Step 5 — Update MVP_TASKS.md

Add one line under `Currently in progress` (or refresh it):

```markdown
- **Team paused at <YYYY-MM-DD>** — handoff doc: `docs/team-handoffs/<NNN>-<date>-<topic>.md` · last round-seal: `<commit hash>` · next-slice target: <task ID or "TBD per handoff">
```

## Step 6 — Commit

Stage explicitly:
```bash
git add docs/team-handoffs/<NNN>-<date>-<topic>.md MVP_TASKS.md
git status --short    # verify only handoff doc + MVP_TASKS.md staged
```

Conventional Commits + AI trailer (HEREDOC):
```bash
git commit -m "$(cat <<'EOF'
chore(team): handoff <NNN> — <topic>

<one-paragraph body: why the team is pausing, what landed in the closing round,
where the next session resumes from. Reference the predecessor handoff if any.>

Assisted-by: Claude Code
EOF
)"
```

If a predecessor handoff exists, also update its "Successor handoff" link to point at this one — same commit.

**Do NOT push** unless a remote is configured + the user explicitly approves.

## Step 6.5 — Clean up team-registry entries

Remove this team's registry entries so `/context-check` no longer reports them as live (heartbeats would also age out via the 10-minute staleness filter, but explicit cleanup is cleaner):

```bash
# Read the team's session IDs from the team config (if still present), then
# remove each one's registry + heartbeat file.
TEAM="<your team name>"
TEAM_CONFIG="$HOME/.claude/teams/$TEAM/config.json"
if [ -f "$TEAM_CONFIG" ]; then
  # Lead's session_id (in team config)
  lead_sid=$(jq -r '.leadSessionId // empty' "$TEAM_CONFIG")
  [ -n "$lead_sid" ] && rm -f "$HOME/.claude/team-registry/${lead_sid}.json" "$HOME/.claude/heartbeats/${lead_sid}.json"
fi
# Other members' session_ids — read each registry file, match by team, remove.
for f in "$HOME/.claude/team-registry"/*.json; do
  [ -f "$f" ] || continue
  if [ "$(jq -r '.team // empty' "$f" 2>/dev/null)" = "$TEAM" ]; then
    sid=$(jq -r '.session_id // empty' "$f" 2>/dev/null)
    [ -n "$sid" ] && rm -f "$HOME/.claude/heartbeats/${sid}.json"
    rm -f "$f"
  fi
done
```

If a predecessor handoff is being resumed later, the next `/team-start` re-spawns teammates → fresh registry entries land via spawn prompts.

## Step 7 — Tell the user

Report:
- Handoff doc at `docs/team-handoffs/<NNN>-<date>-<topic>.md`.
- Team is paused; teammates are idle (already `/session-end`-closed at Step 1).
- Next `/team-start` resumes from this handoff doc.
- Any open decisions / blockers surfaced in the doc.

## Forbidden in this command

- **Running this without explicit user go.** It's a close-out command — same gate as `/session-end` + `/orchestrate-end`.
- **Tearing down mid-work.** Step 1's gate is non-negotiable.
- **Auto-pausing at a natural boundary** (end of phase, end of arc, end of round). User signals; you act.
- **Pushing without explicit approval.** Round-seal commits aren't pushed silently.
- **Skipping the spawn-prompt section** of the handoff doc. The prompts are the load-bearing handoff content — without them, the next `/team-start` has to re-derive coordination state from scratch.
