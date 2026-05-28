# LESSONS.md — AI Interpreter Workbench (the React frontend)

> Full prose for every lesson logged during work in `web/`. The compact index lives in `web/CLAUDE.md` "Lessons logged" table.
>
> **Lesson numbers are stable IDs.** New lessons get the next sequential number. Numbers may be referenced from code comments, commit messages, and cross-references between lessons. **Don't reorder; don't reuse a deleted number's slot.**
>
> **Lessons start at §1.** Each code area has its own lesson sequence — lessons don't carry across code areas.

---

## Lesson format

```markdown
## <a id="N"></a>N. <Short topic> — <one-line rule>

**Date:** YYYY-MM-DD.
**Source slice:** <slice-id or commit hash>.

<2-5 paragraphs explaining: what was discovered, why it matters, how to
apply the rule, what edge cases are still open. Cite file:line references
where applicable.>

**Rule:** <one-sentence summary, same as the heading subtitle>.
```

---

## <a id="1"></a>1. Prettier `--write .` reformats orchestrator-owned area docs — `.prettierignore` them

**Date:** 2026-05-28.
**Source slice:** A.1 (solution + repo scaffold).

During the A.1 web scaffold, running `prettier --write .` across `web/` silently reformatted `web/CLAUDE.md` (padded markdown tables, inserted blank lines) — an orchestrator-territory file the implementer must never touch. It was caught and restored byte-for-byte from the session-start Read, but the root cause is that Prettier's default glob includes markdown and walks the whole area, so any formatter run reaches the area's `CLAUDE.md` / `LESSONS.md`.

Why it matters: `CLAUDE.md` and `LESSONS.md` are orchestrator-owned (root + area `CLAUDE.md` "Implementer must NOT touch"). Silent reformatting by an implementer-run formatter is exactly the territory-drift the staggered-commit model exists to prevent, and it produces noisy diffs on files that should change only via deliberate orchestrator edits.

How to apply: every area that runs a markdown-capable formatter lists its area docs in the formatter's ignore file. For `web/`, `web/.prettierignore` includes `CLAUDE.md` and `LESSONS.md` (added in A.1); any new area doc or new formatter gets the same guard. The mechanism is web-specific: the backend uses `dotnet format`, which targets `.cs` and does not touch markdown, so `server/CLAUDE.md` / `server/LESSONS.md` are not at risk from the backend formatter — but the same rule applies if a markdown formatter is ever added to `server/`.

**Rule:** Add orchestrator-owned area docs (`CLAUDE.md`, `LESSONS.md`) to any markdown-capable formatter's ignore file so an implementer-run formatter can't reformat them.
