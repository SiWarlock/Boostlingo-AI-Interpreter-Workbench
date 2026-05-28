---
description: Report per-teammate context usage for the current team (or all teams). Usage: /context-check [team-name]
allowed-tools: Bash, Read
argument-hint: "[team-name]"
---

Report per-teammate context usage for the current team (or a named team / all teams).

Argument: `$ARGUMENTS` — optional team name. If omitted, defaults to:
- `$TEAM_NAME` env var if set (lead session has this from `/team-start`)
- Else: all teams with active registry entries

## Procedure

1. **Verify the helper script is installed.** If missing, surface the install instruction (the user must install once from `templates/scripts/check-team-context.sh` → `~/.claude/scripts/check-team-context.sh`):

   ```bash
   [ -x "$HOME/.claude/scripts/check-team-context.sh" ] || {
     echo "ERROR: check-team-context.sh not installed."
     echo "Install: cp templates/scripts/check-team-context.sh ~/.claude/scripts/ && chmod +x ~/.claude/scripts/check-team-context.sh"
     exit 1
   }
   ```

2. **Run the helper** with the argument (or empty for auto-detect):

   ```bash
   ~/.claude/scripts/check-team-context.sh ${ARGUMENTS:-}
   ```

3. **Report the output verbatim.** The helper produces:
   - A per-team grouping with each teammate's `ctx_pct`, tier (`OK` / `WARN` / `ACTION` / `HARD-STOP`), trajectory estimate (slices-to-action threshold if history is available), heartbeat age.
   - A one-line aggregate recommendation per team (e.g. `Team frontend-team: WARN — 1 teammate at 71%, ~1 slice to ACTION threshold`).

4. **If invoked by the orchestrator as part of its per-slice flow**, after reporting the output, the orchestrator sends a structured ping to the team lead:

   ```
   SendMessage to: team-lead
   summary: "Slice <hash> context-check"
   message: "<helper output, condensed to per-team aggregate line>"
   ```

   The lead evaluates the report against its threshold logic (per `docs/team-protocol.md` "Context monitoring + auto-cycle"):
   - All teammates `OK` → silent log; no surface.
   - Any `WARN` → emit one-line surface text with trajectory.
   - Any `ACTION` → initiate close-out cycle at the next clean break (this slice already landed, so cycle starts now).
   - Any `HARD-STOP` → halt brief dispatch + cycle immediately.

5. **If invoked manually** (by user or lead for visibility), just report; no action needed.

## Output format

The helper outputs human-readable text by default. For programmatic consumption (the orch's per-slice ping construction), use:

```bash
~/.claude/scripts/check-team-context.sh ${ARGUMENTS:-} --json
```

Add `--history` to include trajectory data (per-slice growth from `~/.claude/team-history/`):

```bash
~/.claude/scripts/check-team-context.sh ${ARGUMENTS:-} --history
```

## Threshold tiers (configurable via env)

| Env var | Default | Meaning |
|---|---|---|
| `CLAUDE_TEAM_CTX_WARN` | `70` | Warning tier — surface one-line text + trajectory |
| `CLAUDE_TEAM_CTX_ACTION` | `75` | Action tier — auto-trigger close-out cycle |
| `CLAUDE_TEAM_CTX_HARD` | `80` | Hard-stop tier — halt dispatch + immediate cycle |

Set in `~/.claude/settings.json` `env` block to override.

## When NOT to invoke this command

- **Solo sessions** — there's no team registry. Output will be empty.
- **Mid-slice** — the trigger lives at slice-boundaries (post-Step-10). Manually invoking mid-slice gives a stale snapshot of the last status-line refresh; not necessarily wrong, but the data isn't slice-boundary-anchored.
- **No teammates spawned yet** — no registry entries → no output. Spawn teammates via `/team-start` first.

## Forbidden in this command

- **Acting on threshold detection unilaterally.** This command REPORTS. The lead acts per `docs/team-protocol.md` "Context monitoring + auto-cycle." A reporter that also triggers close-out would couple data + action — keep them separated.
- **Fabricating context data.** If the helper script returns empty / stale, report it as such. Never guess at ctx%.
- **Editing heartbeat / registry files.** This command is read-only against those files.
