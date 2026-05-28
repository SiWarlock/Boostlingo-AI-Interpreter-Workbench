---
description: Orchestrator-only close-out — verify hot routing, reconcile the task tracker, prep next session.
allowed-tools: Read, Edit, Write, Bash, Grep
argument-hint: ""
---

> **Role guard — ORCHESTRATOR only.** If you are an **implementer**, stop: run `/session-end`, not this. `/orchestrate-end` is the orchestrator's round close-out; the implementer's close-out is `/session-end`.

An implementer just ran `/session-end` and produced a session doc (its recap came to you directly). You (the orchestrator) reconcile planning state and prep the next session. **You ARE the orchestrator** — the one who routed Step 9 items hot during the session and authored the `/tdd` briefs.

`/orchestrate-end` is verification + reconciliation, not aggregation. Hot routing should already have done most of the work. This command catches drift and captures the orchestrator's framing.

> **Note on per-slice context-checks:** the per-slice context-monitoring step (orch → `/context-check` → ping lead) is NOT part of this command. It fires AFTER every slice's Step-10 commit, per the orchestrator's per-slice flow in `docs/orchestrator-briefing.md` "Per-slice context check + lead ping." By the time `/orchestrate-end` runs, many per-slice checks have already fired. `/orchestrate-end` itself just performs the round close-out.

## Step 1 — Locate the implementer's session doc

```bash
ls docs/sessions/
```

Find the highest-numbered file. Read it end-to-end. The "What was built", "Decisions made", "Decisions explicitly NOT made", and "Open follow-ups" sections are critical context.

If no implementer session ran this round (orchestrator-only session — scaffolding, deploy ops, big planning shifts with no `/tdd` cycles), skip to Step 6.

## Step 2 — Verify Step-9 hot-routing landed

Walk the Step 9 summaries you received from the implementer this session. For each categorized item, verify it landed in the destination defined by the **canonical routing matrix in `docs/orchestrator-briefing.md`**:

| Step 9 category | Destination | Verify |
|---|---|---|
| Convention candidate | full prose in `<area>/LESSONS.md` (next anchor) + a one-line index row **with an anchor link** `[topic](LESSONS.md#N)` in `<area>/CLAUDE.md` (never prose in `CLAUDE.md`) | grep the lesson title in `LESSONS.md`; confirm the `CLAUDE.md` row links to it |
| Architecture doc note | `ARCHITECTURE.md §X.Y` prose | git diff the section against pre-session HEAD |
| Future TODO — belongs to a phase | a normal task checkbox in the correct phase/subphase of `MVP_TASKS.md` (not an `Operational TODO` annotation) | grep for the task description |
| Future TODO — next-brief working set | Carry-forward section (with origin marker) | grep |
| Future TODO — out of scope | deferment was escalated + approved → deferred phase / Trims Catalog | grep + confirm escalation happened |
| Cross-doc invariant change | `ARCHITECTURE.md` edit + `<area>/CLAUDE.md` table row | `git log -p` for the relevant section |
| **Completed work** | **Ticked checkbox in `MVP_TASKS.md`** | walk the implementer's "What was built" → confirm each completed task is `[x]` |

Anything that slipped through hot routing — write the fix now (escalate only if it's a deferment or safety finding).

## Step 3 — Reconcile `MVP_TASKS.md` checkbox state

The "Completed work → ticked checkbox" row is the most-likely-to-slip routing. Walk the implementer's session doc "What was built" + "Decisions made" sections. Every completed task should have a `[x]`. Tick any missed ones now (conservative — only `[x]` if work is *complete and verified*, not "mostly done").

If a slice landed partial work, leave the box `[ ]` and add a parenthetical note: `(partial: <what landed; what's still missing>; deferred to <slice or phase>)`.

## Step 4 — Append a Log entry to `MVP_TASKS.md`

The implementer's session doc is the technical narrative. The Log entry is the **orchestrator's framing** — what landed at the planning level, decisions made, scope shifts, what's now unblocked or blocked.

Format:

```markdown
### YYYY-MM-DD — <session topic, orchestrator's framing>

- <bullet: what completed at the planning level>
- Decisions made: <if any>
- Scope shifts: <if any items moved between phases, between MVP and nice-to-haves>
- New blockers / open questions: <if any>
- Next session target: <what's queued up>
- Reference: implementer session doc `<NNN>-<date>-<topic>.md` for technical detail.
```

## Step 5 — Update planning state

- **Decisions tabled** — resolved entries move to the Log entry above (with the resolution); new entries get added with rationale.
- **Carry-forward to upcoming briefs** — add items the next `/tdd` brief MUST fold in. New entries include an origin marker `(origin: YYYY-MM-DD <slice-id>)`; multi-slice spreads also include `last-consumer-slice: <id>`.
- **Trims / Nice-to-Haves Catalog** — add entries for anything deferred this session with come-back guidance.
- **"Currently in progress"** — update with the last commit hash, suite count, next session target, anything blocking.

## Step 5.5 — Triage Carry-forward to upcoming briefs

The Step-9 routing matrix routes operational/optimization items INTO the Carry-forward section (Step 5). There's no symmetric step that routes them OUT. Without active triage the section accumulates monotonically and stops being useful for next-brief authoring. Step 5.5 closes the loop.

Walk every bullet under `## Carry-forward to upcoming briefs`. Read each item + its origin date marker. Propose ONE of five outcomes to the user, with a one-line rationale:

| Outcome | When | Action |
|---|---|---|
| **(a) DELETE** | Item was completed since it landed in Carry-forward | Remove the bullet; cite where it was completed |
| **(b) KEEP** | Item is needed in the IMMEDIATELY next `/tdd` brief (next 1–2 slices) | Leave in Carry-forward; fold it into the next brief |
| **(c) INLINE-TARGET** | Item belongs in a specific phase's scope | **Convert it into a real task checkbox in that phase/subphase** (a `- [ ]` entry, phrased as a unit of work); remove from Carry-forward. Do NOT leave it as an `Operational TODO` annotation — it becomes a first-class task. |
| **(d) DEFER** | Item is genuinely post-current-sprint | **Deferment → escalate to the human.** On approval, move to the deferred-phase backlog / Trims with an origin note; remove from Carry-forward |
| **(e) SPREAD** | Item spans multiple future slices | Annotate with `last-consumer-slice: <id>`; keep in Carry-forward; auto-delete at that slice's `/orchestrate-end` |

Per-item: apply the outcome directly. DELETE/KEEP/INLINE/SPREAD are orchestrator-handled; **DEFER escalates** (deferment approval).

After the walk, surface counts: *"Triage complete: K deleted, M inlined, J deferred, S spread, N kept."* If N (kept) > 5 AND triage didn't reduce the section materially, surface an **informational** warning (not blocking — different phases accumulate at different rates).

This step runs BEFORE the round commit (Step 7) so triage outcomes land in the same commit.

## Step 6 — Optionally create an orchestrator-side session doc

Decision criterion: did substantial *orchestrator-side* work land this round? Substantial = scaffolding refactor, deploy ops, infrastructure changes, big planning shifts, or a session that ran orchestrator-only with no `/tdd` cycles.

If YES → create `docs/sessions/<NNN>-<date>-<topic>.md` (next sequential NNN, shared sequence with implementer docs). Update the predecessor session doc's Successor link.

If NO → no orchestrator-side doc needed.

## Step 7 — Commit the round + push

After reconciliation, the orchestrator-side working tree has:
- `MVP_TASKS.md` updates (Log entry, box ticks, Carry-forward additions + Step-5.5 triage relocations, "Currently in progress" refresh, Decisions tabled changes)
- Any hot-routed `<area>/LESSONS.md` + `<area>/CLAUDE.md` index additions that haven't ridden a slice commit yet
- Any hot-routed `ARCHITECTURE.md` prose edits that haven't ridden a slice commit yet
- Any `/tdd` brief file(s) authored this round in `docs/briefs/<NNN>-<task-id>-<topic>.md` (incl. in-place refreshes of a stale brief)
- Optionally a new `docs/sessions/<NNN>-<date>-<topic>.md` from Step 6

Stage these explicitly (do NOT use `git add -A`):

```bash
git add MVP_TASKS.md \
        <area>/LESSONS.md \
        <area>/CLAUDE.md \
        ARCHITECTURE.md \
        docs/briefs/<NNN>-*.md \
        docs/sessions/<NNN>-*.md
git status --short  # Verify only orchestrator-domain files staged
```

Author the commit message — Conventional Commits + `Assisted-by: Claude Code` trailer — capturing the orchestrator's framing of the round:

```bash
git commit -m "$(cat <<'EOF'
docs(tasks): <round topic — orchestrator framing>

<body — what was reconciled, scope decisions, transition state, next
session target. Reference the implementer's session doc by NNN.>

Assisted-by: Claude Code
EOF
)"
```

Then **push to none configured only**:

```bash
git push <remote> <branch>
```

This is the round's terminal commit. Confirm with the user that the push landed before declaring the round closed.

## Step 8 — Confirm with user + prep next /tdd brief

Summarize the close-out:
- What was reconciled (boxes ticked, Log entry appended, Carry-forward additions)
- Step-5.5 triage outcomes + threshold-warning state
- Anything that slipped through hot routing (and the fix that landed)
- Whether an orchestrator-side session doc was created
- Round commit hash + push confirmation
- Suggested next slice — reference `MVP_TASKS.md` "Currently in progress" + "Carry-forward" (now triaged)

Once the user confirms, author the next `/tdd` brief per `docs/tdd-brief-template.md` (saved as `docs/briefs/NNN-<task-id>-<topic>.md`) if the user wants to continue. Otherwise, the round closes here.

## Forbidden in this command

- **Re-aggregating Step 9 items.** They were already routed hot. This command verifies, doesn't re-route.
- **Pushing to the wrong remote.** Push to none configured only.
- **Authoring the next /tdd brief before the user confirms close-out is clean.** Reconciliation must complete first.
- **Skipping Step 2 or Step 3 verification.** The whole point of this command is catching what slipped through hot routing.
- **Skipping Step 5.5 triage.** If the Carry-forward section is never drained, it accumulates monotonically and stops being useful.
- **Deferring without escalating.** A DEFER (scope cut) is a deferment approval — escalate to the human; never cut scope agent-only.
