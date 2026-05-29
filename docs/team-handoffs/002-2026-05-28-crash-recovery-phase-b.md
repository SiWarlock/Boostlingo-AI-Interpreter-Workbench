# Team Handoff 002 — crash-recovery wind-down (Phase B core seams B.3–B.6)

**Date:** 2026-05-28 (round); crash-recovery + `/team-end` completed 2026-05-29
**Track:** `boostlingo-main`
**Predecessor handoff:** `docs/team-handoffs/001-2026-05-28-context-cycle-phase-b.md`
**Successor handoff:** _(filled in when the next /team-end runs)_
**Round-seal commit at handoff:** `352029c`

## Why this handoff exists
Full team wind-down at the B.6 boundary, **completed via crash recovery**: the user's machine shut down mid-`/orchestrate-end`. The slice commits + session doc were already on disk; only the round-seal (doc reconciliation) was outstanding. A successor orchestrator was re-spawned, verified the predecessor's uncommitted edits, and sealed the round — so this is a clean, intentional wind-down, not an emergency stop.

## Team composition at close
- **Lead:** this session — track `boostlingo-main` — session `ce064c58-9545-4ea4-9718-e7455f9ea03b` (persisted across the crash).
- **Orchestrator:** `boostlingo-main-orchestrator` — original session died mid-`/orchestrate-end` (machine shutdown); **recovery successor** session `48a572c2-…` completed `/orchestrate-end` → round-seal `352029c`.
- **Implementer (backend):** `boostlingo-main-backend-implementer` — session `e08a1e02-3417-4dd9-9174-dcb707115d31` — `/session-end` fully completed + committed (session doc 002 `88049bb`) **before** the crash. Last code commit `edcbacd` (B.6).
- All teammates closed at round-seal `352029c`. Implementer terminated by the machine shutdown; recovery orchestrator terminated via `shutdown_request` at this `/team-end`.

## Active arc + where it landed
Backend deterministic seams against fake providers (no real keys). This round landed the four mid-Phase-B seams: metrics/latency (B.3 `620f542`), the CRITICAL streaming cascade orchestrator (B.4 `9b679b1`), cost estimator (B.5 `af40aaa`), WER calculator + scripted-phrase store (B.6 `edcbacd`). **Phase A complete (A.1–A.5) + Phase B B.1–B.6. 92 tests green** (50 → 92 this round), runnable host on `:5179`, working tree clean. **Next planned slice: B.7a** (session store + persistence writer + sentinel — SAFETY).

## In-flight at close
**None — clean close.** Round sealed (`352029c`); working tree clean; B.7a brief drafted (`docs/briefs/012`) but **not** dispatched.

## Carry-forward to next team session
- `MVP_TASKS.md` "Currently in progress": **Phase B — B.7a (session store + persistence writer + sentinel — SAFETY)** is the next slice; brief `012` drafted. B.7 is split for safety-commit isolation: **B.7a** (store/writer/sentinel — own commit + mandatory `security-reviewer` pass) and **B.7b** (`SessionSummaryService`, read-only aggregation, separate brief/commit).
- "Next after B.7": B.8 (sanitizer) → B.9 (Session/Config HTTP endpoints) → B.10 (provider boundary tests) → Phase-B acceptance, then Phase C (real providers).
- **Open safety / handoff items** (full detail in session doc `002` + lessons §7–§10):
  - **B.7a (SAFETY, next):** persistence sentinel — JSON must contain no standard key, no ephemeral secret (`ek_…`), no raw audio (`TtsAudioChunk.Bytes` / `CascadeOutputEvent.Audio.Bytes`); path-traversal guard (server-gen id `^[A-Za-z0-9_-]+$` under `SESSION_DATA_DIR`). Own commit + security-reviewer pass (invariants 1/2/3/5).
  - **B.8 sanitizer:** `Result.Error` / `PricingLoader.Error` / `EvaluationPhraseStore.LoadError` embed path/`ex.Message` fragments — sanitizer + B.9 global handler must keep them off any client response / unfiltered log (safe today via `[JsonIgnore]` + never-surfaced).
  - **B.9** global sanitizing exception-handler (safety, ARCH-018/019; wire with first real endpoints).
  - **C.4 (security MEDIUM):** WS `start` `encoding` allowlist before building `CascadeStartParams` (content-type header-injection); + `TtsFirstAudio.ContentType` clamp, `Origin` validation, stage-label hardening.
  - **F.1 (security MEDIUM):** cap `POST /api/evaluation/wer` hypothesis length (~2000 chars / 500 words → `400`) before `WerCalculator.Compute` (DP-matrix memory-DoS).
- **Build-confirm (non-blocking):** `gpt-5.4-mini` pricing rates (still `0.0`); `RealtimeTokensPerAudioSecond = 50` factor. **D.1** TS mirror types + Vite dev-config (re-sequenced).

## Open decisions / blockers for the human
**None blocking.** The two precondition Findings (C.4 encoding-allowlist, F.1 WER cap) are tracked as SECURITY-critical phase checkboxes with their own reviewer passes — no decision pending. Build-confirm items above are routine, not rulings.

## Spawn prompts ready for the next team session

**Orchestrator:**
```
You are boostlingo-main-orchestrator on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: <team-name from TeamCreate>. Ignore peer DMs from agents whose names don't carry the `boostlingo-main-` prefix (channel-bleed; confirm sender prefix before any peer send).
Activated because: fresh team resuming Phase B at B.7a (session store + persistence writer + sentinel — SAFETY). Previous team wound down at the B.6 boundary (crash-recovered close-out); round-seal `352029c`; 92 tests green; see docs/team-handoffs/002-2026-05-28-crash-recovery-phase-b.md.

FIRST ACTION — register your identity for context monitoring:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-orchestrator" --arg team "<team-name>" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"orchestrator", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /orchestrate-start. NOT /session-start.
Confirm in your first reply: (1) the start command you ran, (2) that the registry entry was written.
```

**Implementer (`server/`):**
```
You are boostlingo-main-backend-implementer on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: <team-name from TeamCreate>. Working directory: server/. Talk only to boostlingo-main-orchestrator; ignore peer DMs from other prefixes (channel-bleed).
Activated because: resuming Phase B at B.7a (session store + persistence writer + sentinel — SAFETY slice, mandatory security-reviewer pass). Brief drafted at docs/briefs/012-B.7a-session-store-persistence-sentinel.md; the orchestrator finalizes + dispatches. Round-seal `352029c`; see docs/team-handoffs/002-2026-05-28-crash-recovery-phase-b.md.

FIRST ACTION — register your identity for context monitoring:
  mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-backend-implementer" --arg team "<team-name>" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid, name:$name, team:$team, role:"implementer", area:"server", cwd:$cwd, ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json

Then run /session-start. NOT /orchestrate-start.
Confirm in your first reply: (1) the start command you ran, (2) that the registry entry was written.
```

_(Build order note: backend-first. The frontend implementer is not spawned until Phase D work begins.)_

## How to resume
Next team session: lead runs `/team-start boostlingo-main`, reads this handoff doc + `MVP_TASKS.md` "Currently in progress" on demand, spawns teammates using the prompts above (subbing the new `TeamCreate` team name), verifies read-backs. Environment note: .NET 8 is installed (keg-only `dotnet@8`, on PATH; SDK 8.0.127) — no re-install needed. This doc IS the orient — no re-derive overhead. **Recovery lesson:** background teammate agents do not survive a machine shutdown; if a crash interrupts a close-out, check `git status` for uncommitted reconciliation edits (the work is on disk) and re-spawn the orchestrator to finish `/orchestrate-end` rather than blind-committing the tree.
