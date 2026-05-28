---
description: Run tests by class. cwd-aware. Usage: /run-tests [unit|integration|all]
allowed-tools: Bash
argument-hint: "[unit|integration|all]"
---

Run tests by class. **cwd-aware** — runs the right test runner for whichever code area you're in.

Argument: `$ARGUMENTS` — see the mapping table(s) below. Default: `unit`.

## Step 0 — Detect mode

```bash
case "$(pwd)" in
  */web|*/web/*) MODE=the React frontend ;;
  *)                                                      MODE=the .NET backend ;;
esac
```

Announce the detected mode before running.

---

## the .NET backend mode mapping

| Argument | Command |
|---|---|
| (empty / `unit`) | `dotnet test --filter "Category!=Integration"` |
| `integration` | `dotnet test --filter "Category=Integration"` |
| `all` | `dotnet test` |
| <other class / marker> | `<command>` |

## the React frontend mode mapping

<!-- Delete this table for a single-code-area project. -->

| Argument | Command |
|---|---|
| (empty / `unit`) | `npm run test` |
| `integration` / `e2e` | `npm run test` |
| `all` | `npm run test` |

If an argument names a class that belongs to the *other* mode, **ERROR** with a clear message naming the expected cwd.

---

<!-- ▼ EXAMPLE BLOCK: test-class discipline notes — OPTIONAL. Some test classes
     need preconditions (a live external dependency, an env var, a slow browser).
     The source project documented things like: "the live-attack class needs a
     reachable target + a bearer env var, else it skips with a clear message;"
     "the visual-smoke class is slow — run per-PR, not per-commit." Add the
     project's own per-class discipline notes here, or delete this block. ▲ -->

## Output

Report:
- Mode (which code area)
- Test count + class
- Pass / fail counts
- First ~20 lines of any failure
- Total duration
