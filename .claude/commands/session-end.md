---
description: Implementer-only close-out — TDD audit, cross-doc audit, wiring + Step-9 verification, session doc, /preflight.
allowed-tools: Read, Edit, Write, Bash
argument-hint: ""
---

> **Role guard — IMPLEMENTER sessions only.** If you are the **orchestrator**, stop: run `/orchestrate-end`, not this. `/session-end` writes the implementer's session doc; the orchestrator's round close-out is `/orchestrate-end`.

The user has indicated the implementer session is wrapping. Capture state.

`/session-end` is **technical close-out only** — TDD audit, cross-doc invariant audit, Step-9 routing verification, session doc creation, `/preflight`. **Do NOT touch `MVP_TASKS.md`** — the orchestrator owns that file via `/orchestrate-end`. The handoff: this command produces the session doc + recap; the user pastes the recap to the orchestrator session, which runs `/orchestrate-end`.

Procedure:

1. **Recap to the user** what landed this session in 3–5 bullets. The user pastes this to the orchestrator session as input for `/orchestrate-end`. **Do NOT include any context-usage estimate or self-reported ctx %** — context is monitored externally via the canonical `/context-check` heartbeat path; self-reporting is forbidden per root `CLAUDE.md` "Canonical context source — NO self-reporting." Recap is work-completed only: features built, tests added, decisions made, follow-ups surfaced.

2. **Self-audit TDD compliance** for code changes this session:
   - For each non-trivial code change, did a corresponding test land *first*?
   - Non-deterministic behavior is exempt from unit testing — but the project's non-deterministic-coverage path should still have been followed.
   - If a test was written *after* the implementation: flag it. Note in the session doc: *"TDD violation: <file> implemented before tests existed."*
   - If TDD was skipped on something safety-critical: surface it as a blocker, not just a note.

2.5. **Cross-doc invariant audit.** Read the "Cross-doc invariants" table in `<area>/CLAUDE.md`. For each model listed, check whether its field list (added/removed/renamed fields) changed this session. If yes, verify the corresponding `ARCHITECTURE.md` section was updated in the same set of commits. If a model field changed without a doc edit:
   - Flag it as a discipline violation.
   - List the affected model + section + the specific fields that changed.
   - Surface to the user with a recommendation: update the doc now, or accept the drift with an explicit ADR-style note.
   - The session doc must annotate this as an open follow-up.

2.6. **Verify Step 9 routing — surface for the orchestrator, don't re-route.** Per the Step-9 routing matrix in `docs/orchestrator-briefing.md`, the orchestrator routes each Step 9 item *hot* during the session. By the time `/session-end` runs, the orchestrator should already have written each item to its destination. **`/session-end` doesn't re-route — it surfaces the categorized list one more time** so the orchestrator's `/orchestrate-end` can verify nothing slipped.

   For each slice this session, list every Step 9 categorized item with its expected destination. The categorized list goes into the session doc's "Open follow-ups" section. **Do NOT modify `MVP_TASKS.md` or `<area>/LESSONS.md` from this command.**

2.7. **Wiring / reachability audit.** For each feature built this session, confirm `/tdd` Step 7.5 was satisfied — the feature is reachable from a real production entry point (route, job, UI handler, exported API, contract function selector, deploy step), not only from its own tests. For anything still tested-but-unwired, list the specific entry point that needs wiring as an **open follow-up** categorized "Future TODO — belongs to a phase" so the orchestrator lands it as a real task. A green suite over an unreachable feature is a silent gap — surface it here.

3. **ALWAYS create a session doc** at `docs/sessions/<NNN>-<YYYY-MM-DD>-<topic>.md`. This is required, not optional. Compute `<NNN>` as the next sequential number — `ls docs/sessions/`, find the max NNN prefix, increment, zero-pad to 3 digits.

   The doc must include these sections:
   - **Header** — date, phase, predecessor + successor session links
   - **Why this session existed** — what problem prompted it
   - **What was built** — **Files created** (list with one-line purposes) + **Files modified** (list with what changed)
   - **Decisions made** — with rationale for each
   - **Decisions explicitly NOT made** — deferred to a future session, with rationale
   - **TDD compliance** — clean or violations (matches §2 above)
   - **Reachability** — for each feature, the entry point it's reachable from (§2.7); list any tested-but-unwired gaps
   - **Open follow-ups** — what's left for future-you, including the Step-9 categorized list from §2.6 + any wiring tasks from §2.7
   - **How to use what was built** — only when relevant

   After writing the new session doc, **update the predecessor session doc's "Successor session" link** to point at the new file. The link convention is bidirectional.

4. **Run `git status --short`** to confirm: every file the session doc lists as created/modified shows up in the diff. If something's missing, the doc is incomplete — fix before the user sees it.

5. **Invoke `/preflight`** as the final gate. If it fails, surface the failure and **do not finalize the session doc as "ready"** — annotate it as `incomplete: preflight failures` with the specific failures listed.

6. **Hand off to the orchestrator** (directly — single-operator fallback: paste it across):
   > *"`/session-end` complete. Session doc at `docs/sessions/<NNN>-<date>-<topic>.md`. Recap + Step-9 categorized list + any wiring follow-ups are in the doc's Open follow-ups section. Over to you for `/orchestrate-end`."*

**Commit posture for this command:**

Slice commits already happened during the session — `/tdd` Step 10 commits each slice immediately after the user approves Step 9. By the time `/session-end` runs, the only implementer-side artifact left to commit is the session doc itself (and any test-fix from the §2 audit).

```bash
git add docs/sessions/<NNN>-<date>-<topic>.md
# + any test-fix files from the audit
git status --short  # Verify only session doc + audit fixes staged
```

Commit with Conventional Commits + `Assisted-by: Claude Code` trailer. Topic prefix `docs(sessions)` or `chore(sessions)` typically.

**Do NOT push.** Push happens at end of round (the orchestrator's `/orchestrate-end`).

**Do NOT modify `MVP_TASKS.md`, `<area>/LESSONS.md`, or `ARCHITECTURE.md` from this command.** Those are orchestrator-owned. Step 2.6 *surfaces* what should be in those files; the orchestrator *writes* via `/orchestrate-end` (or hot during the session).
