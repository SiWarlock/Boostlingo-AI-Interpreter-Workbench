---
description: Full preflight gate — sync deps, lint, format-check, type-check, test.
allowed-tools: Bash, Read
argument-hint: ""
---

Run the full quality gate for the current code area. **cwd-aware** — runs the right toolchain for whichever code area you're in.

Stops on first failure. Reports per-step pass/fail with the first ~20 lines of error output. Does NOT auto-fix on failure.

## Step 0 — Detect mode

```bash
case "$(pwd)" in
  */web|*/web/*) MODE=the React frontend ;;
  *)                                                      MODE=the .NET backend ;;
esac
```

Announce the detected mode to the user before running steps. If the mode looks wrong for the user's intent, surface the cwd and ask before proceeding.

---

## the .NET backend mode (cwd is `server/` or repo root)

### Step 1 — Sync dependencies
```bash
dotnet restore
```

### Step 2 — Lint
```bash
dotnet format --verify-no-changes
```

### Step 3 — Format check
```bash
dotnet format whitespace --verify-no-changes
```

### Step 4 — Type check
```bash
dotnet build
```

### Step 5 — Test
```bash
dotnet test
```

---

## the React frontend mode (cwd is `web/` or below)

<!-- Delete this whole section for a single-code-area project. -->

### Step 1 — Sync dependencies
```bash
npm install
```

### Step 2 — Lint
```bash
npm run lint
```

### Step 3 — Format check
```bash
npm run format:check
```

### Step 4 — Type check
```bash
npm run typecheck
```

### Step 5 — Test
```bash
npm run test
```

### Step 6 — Build
```bash
npm run build
```

<!-- Keep a build step only if the area's build catches a class of errors the
     type-checker alone doesn't (e.g. a frontend production build). -->

---

## Output

**Success:**
> "Preflight clean (<mode>): lint ✓ + format ✓ + types ✓ + N tests pass"

**Failure (either mode):**
> "Preflight failed at Step N: <step name>"
> <first ~20 lines of error output>

## Forbidden in this command

- **Auto-fixing on failure.** The gate exists to catch problems; fixing them silently defeats the purpose.
- **Modifying baseline / ignore files to suppress failures.** Fix the underlying error.
- **Skipping steps.** Run in order; stop on first failure.
- **Cross-mode contamination.** Don't run one area's toolchain from another area's cwd. If cwd is wrong, fail loud with a clear message.
