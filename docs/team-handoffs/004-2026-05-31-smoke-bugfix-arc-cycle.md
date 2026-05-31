# Team Handoff 004 — Real-Key Smoke Bug-Fix Arc (wholesale cycle)

**Date:** 2026-05-31
**Track:** `boostlingo-main`
**Predecessor handoff:** `docs/team-handoffs/003-2026-05-31-ui-design-pause.md`
**Successor handoff:** _(filled in when the next /team-end runs)_
**Round-seal at handoff:** ⚠️ **PENDING** — latest slice commit `79ef9c7` (052). The orchestrator's `/orchestrate-end` round-seal (the deferred doc routing) lands at the close-out; if it can't fully complete (orch hit 75%), the **fresh orchestrator finishes it per crash-recovery** — all slice commits are landed + HEAD-verified, and `MVP_TASKS.md` "Currently in progress" carries the explicit "still to write at the seal" list.

## Why this handoff exists
**Wholesale context cycle mid-arc** — lead hit ~84% (user raised the lead ceiling to 80-85, see [[lead-ctx-threshold-80-85]]), orch hit 75% ACTION. Both due. The team is mid the **real-key smoke bug-fix arc** (the live smoke surfaced real integration bugs). Slices are landing fast; this is a clean wholesale cycle so a fresh full team continues.

## Team composition at close
- **Lead:** this session (track `boostlingo-main`).
- **Orchestrator:** `boostlingo-main-orchestrator` — closing at `/orchestrate-end` (round-seal may be partial — fresh orch finishes).
- **Implementer (backend):** `boostlingo-main-backend-implementer` — `/session-end` at close (last slice 052 `79ef9c7`).
- **Implementer (frontend):** `boostlingo-main-frontend-implementer` — finishing **053-B** (realtime transcript fix + DC diagnostic); `/session-end` once it lands.

## Active arc + the SCOPE → see MVP_TASKS
**`MVP_TASKS.md` "Currently in progress" is the COMPLETE, RECONCILED source of truth** (the orch did a 15-item reconciliation pass + rewrote the stale section to the live arc). Read it first. Summary:
- **DONE this arc (HEAD-verified):** 048 stop-hardening `ccd9cb6` · 049 frontend smoke bugs `27cab1f`/`ccfcc17` · 051 model-name fix `1b352e0`/`8c46d13` · 052 empty-transcript tolerance `79ef9c7` · 050-frontend `7dc398e`.
- **IN FLIGHT at close:** 053-B realtime transcript+metrics (frontend; A/C audio parts need a live data-channel stream — investigation captured for the fresh team).
- **QUEUED (sequence):** 050-backend (mode-switch endpoint + Flow-G `RecordModeTransition` timeline; pre-approved design) → **H.3 session-history view (PULLED UP, user-requested)** → metrics-polish (latency-headline framing + per-stage-n/a) → G.3 demo / G.5 write-up / G.4 stability → **Phase I auto-VAD**.

## ⭐ LIVE-ONLY state (NOT in MVP_TASKS — carry this)

### Server ownership (the lead owns the running app)
- **Backend running on `:5179`** (current bg task `bze9d6uyu`), started with the user's keys **sourced from `.env`**:
  ```bash
  cd server/AiInterpreter.Api && lsof -ti tcp:5179 | xargs kill -9 2>/dev/null
  set -a && source ../../.env && set +a && export OPENAI_TRANSLATION_MODEL=gpt-5-nano && dotnet run --urls http://localhost:5179
  ```
  **Restart it on EVERY backend fix commit**, then tell the user to re-test. (The `.env` has NO auto-loader — G.2b; and its `OPENAI_TRANSLATION_MODEL` still says the old `gpt-5.4-nano`, hence the `export` override — harmless now since the ConfigService list + frontend default are fixed, but keep the override.)
- **Frontend `:5173`** (`cd web && npm run dev`). Vite proxies `/api` → `:5179`.
- After a frontend code change, Vite hot-reloads; after a backend change, the lead restarts `:5179`.

### Where the smoke stands (live)
- **Cascade: WORKS.** Real Deepgram STT → gpt-5-nano translation → TTS, end-to-end. 051 fixed the model; 052 fixed the false-fail. (User was re-confirming a clean `completed` turn at cycle time.)
- **Realtime: audio WORKS** (WebRTC + interpretation), but **transcripts + metrics don't render** (053 — the GA transcript-event mapping + event reporting; audio-delta path is fine).
- Mode-switch mid-session: fails until 050-backend (endpoint 404s).
- Cost/metrics were `n/a`/missing only because turns were FAILED — should populate once turns succeed (verify post-052).

### Chat-only decisions (load-bearing — the fresh team can't derive these)
- **Finding 2c → OPTION B:** mode-switch endpoint `POST /sessions/{id}/mode` + wire the deferred Flow-G `ModeTransitionEvent` timeline (not just the minimal field). (050-backend.)
- **Translation models → `gpt-5-nano` + `gpt-5-mini`** (user chose). `gpt-5.4-*` are real but NOT on the user's key (→ 400). Realtime (`gpt-realtime`/`-mini`), TTS (`gpt-4o-mini-tts`), transcribe (`gpt-4o-mini-transcribe`) all confirmed REAL on the key — don't touch. Real rates: gpt-5-nano $0.05/$0.40, gpt-5-mini $0.25/$2.00 per 1M.
- **Phase I (auto-VAD): BOTH modes get auto-VAD + KEEP a manual Start/Stop toggle** (auto=hands-free/simultaneous, manual=precise measurement). NO Pipecat (Python; our stack is .NET — native VAD). Realtime=server-VAD, cascade=Deepgram endpointing. Revises locked ARCH-003 (user-approved).
- **H.3 session-history PULLED UP** (user asked twice). After cascade/realtime fixes, before deeper polish/G/Phase I.
- **Lead context ceiling raised to ~80-85%** ([[lead-ctx-threshold-80-85]]).
- **Latency-headline framing finding:** the `<3s`/`<1.5s` target + coloring belongs on **speech-end→first-audio** (responsiveness), NOT total-turn (which includes talking). Verify vs ARCH-013. (metrics-polish.)

## In-flight at close
053-B (frontend realtime). The orch sends a "fully closed-ready" signal once it lands. Otherwise nothing mid-slice.

## Open decisions / blockers for the human
None blocking. The user is actively smoke-testing with real keys (keys in `.env`, live API spend OK'd). Next user-facing milestone: confirm a clean `completed` cascade turn (052), then re-test realtime once 053 + the realtime fixes land.

## Spawn prompts for the fresh team
**Orchestrator:**
```
You are boostlingo-main-orchestrator on the AI Interpreter Workbench agent team.
Track: boostlingo-main. Team: boostlingo-main. Ignore non-`boostlingo-main-` peer DMs (channel-bleed).
Activated because: wholesale cycle mid the real-key smoke BUG-FIX ARC. Read MVP_TASKS "Currently in progress" (reconciled, complete) + docs/team-handoffs/004-2026-05-31-smoke-bugfix-arc-cycle.md. Predecessor round-seal may be PARTIAL — FIRST verify + finish it per crash-recovery (git status for uncommitted doc routing; the "still to write at the seal" list is in MVP_TASKS; slice commits 048/049/051/052/050-frontend all landed+HEAD-verified). Then continue the queue: finish 053 → 050-backend → H.3 → metrics-polish → G.3/G.5 → Phase I. The LEAD owns the live :5179 server (restart on backend commits). Commit discipline: `command git commit -F` + `git rev-parse HEAD` verify, ONE Bash/msg, real hashes only; bodies-to-lead can drop (keep facts in summary line + on disk).
FIRST ACTION — register: mkdir -p ~/.claude/team-registry && jq -n --arg sid "$CLAUDE_CODE_SESSION_ID" --arg name "boostlingo-main-orchestrator" --arg team "boostlingo-main" --arg cwd "$(pwd)" --arg ts "$(date -u +%s)" '{session_id:$sid,name:$name,team:$team,role:"orchestrator",cwd:$cwd,ts:($ts|tonumber)}' > ~/.claude/team-registry/${CLAUDE_CODE_SESSION_ID}.json
Then run /orchestrate-start. Confirm: (1) start command, (2) registry written, (3) round-seal state (complete vs needs-finishing).
```

**Implementer (backend, `server/`):**
```
You are boostlingo-main-backend-implementer. Track/Team: boostlingo-main. cwd server/. Talk only to boostlingo-main-orchestrator.
Activated because: smoke bug-fix arc continues. Next backend work: 050-backend (mode-switch endpoint POST /sessions/{id}/mode + wire Flow-G ModeTransitionEvent — design pre-approved) + H.3's list-sessions endpoint, per the orch's briefs. Read MVP_TASKS "Currently in progress" + handoff 004. Commit discipline: `command git commit -F` + HEAD-verify, ONE Bash/msg, real hashes only; never self-report context. Do NOT start the backend server — the LEAD owns :5179.
FIRST ACTION — register (role implementer/area backend), then /session-start.
```

**Implementer (frontend, `web/`):**
```
You are boostlingo-main-frontend-implementer. Track/Team: boostlingo-main. cwd web/. Talk only to boostlingo-main-orchestrator.
Activated because: smoke bug-fix arc continues. Next frontend work: finish 053 (realtime transcript+metrics — Fix B session.update transcription was confirmed; the audio A/C parts need live DC diagnosis), the 050-frontend mode-toggle already landed, then H.3 session-history view + the metrics-polish (latency-headline framing). Read MVP_TASKS + handoff 004. Commit discipline + never self-report context.
FIRST ACTION — register (role implementer/area frontend), then /session-start.
```

## How to resume
Next session: lead runs `/team-start boostlingo-main`, reads this doc + `MVP_TASKS.md` "Currently in progress", spawns orch + backend impl + frontend impl (all three areas — the arc is cross-area), verifies read-backs, **takes over the live `:5179` server** (restart per the command above). The orch's first job is to verify/finish the predecessor round-seal.
