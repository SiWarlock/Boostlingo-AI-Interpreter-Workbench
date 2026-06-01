# Session 024 — Orchestrator handoff: G.4 soak-harness CODE COMPLETE (086–093) → the live run + G.5 next

- **Date:** 2026-06-01
- **Role:** orchestrator (boostlingo-main) — G.4 code-round seal + CYCLE-FIRST close-out
- **Type:** orchestrator handoff doc (the fresh successor reads this on `/orchestrate-start`, alongside `MVP_TASKS.md` "Currently in progress" + the 2026-06-01 G.4 Log entry)
- **Predecessor orch handoff:** doc `023` (Phase-J seal → G.4 design framing)
- **Successor:** the fresh orchestrator (re-spawned by the lead) — present for the **live run** (route any run-Finding into a fresh-FE fix slice) + then routes the **G.5 write-up**.

## Round just sealed — G.4 5-min synthetic soak-harness CODE COMPLETE
Round commit: this `docs(tasks)` seal. **server 488 / web 394 green.** All 8 slice hashes independently rev-parse + suite-verified by the orch; zero bad commits.

**The harness (dev-only `?soak=1`)** drives a scripted 5-min bidirectional EN↔ES conversation through the REAL pipeline per mode → a structured `SoakReport` (stability: no-disconnect/no-drift/no-leak + latency + cost + WER-via-script). 8 slices atop `938d7fc`:
- **086** dev `POST /api/dev/tts` (`1f18eaa`) · **087** soak engine core (`0d64307`) · **088** `getUserMedia`-DI-seam injection (`5b026a2`) · **089a** runner (`624cecc`) · **090** additive `/wer` explicit-reference path (`6b83ac6`, security-reviewer clean) · **089b/091** live composition + dev entry (`430181b`) · **092** realtime output-token store-surface (`7eb38ce`) · **093** overlap (decision-2A) + precise disconnect (`0767ac1`).
- Lesson web **§35** (FE smoke-shell-over-pure-core); server **§27** amended. Briefs **086–093**. Design + the 4 user sub-decisions + the WER fork (iii) + the output-audio plan: `docs/g4-harness-design.md`.

## ⭐ (a) HARNESS RUN-READINESS (how the live run works)
- **Entry:** `?soak=1` (dev-only, `import.meta.env.DEV` + query-gated) → renders `SoakPanel` (instead of the normal `App`) → pick a mode → "Run soak" → drives the scripted run → renders the `SoakReport` (the 3 ARCH-020 booleans + latency-slope/`overlapMeasured`/`overlapBasis`/overlaps/skew/leak/WER) + the full JSON.
- **Backend `:5179` must be live** with real keys (+ `gpt-5-nano` translation + Debug env). The harness uses the **086** `POST /api/dev/tts` (synthesize+cache the script audio — Development-gated, unmapped in Production) + the **090** `/wer {sessionId, reference, hypothesis}` explicit-reference path.
- **Audio:** synthesized once via the integrated OpenAI TTS (EN/ES voices), cached (generate-on-first-run + gitignore), reused across both modes (fair/repeatable), injected at the `getUserMedia` boundary → the real cascade worklet + realtime WebRTC track.

## ⭐ (b) IMMEDIATE PLAN
1. **The LEAD drives the live real-key 5-min × both-modes run NEXT** (user pre-authorized, autonomous). Restart Vite (`:5173`) + ensure `:5179` is live; hard-refresh (Cmd+Shift+R) the tab.
2. **Fresh orch is present** to route any run-Finding into a fix slice (fresh FE) — the first sustained soak is genuinely likely to surface disconnect/drift/leak items (the cycle-first rationale).
3. **Then route the G.5 write-up** with the run numbers (the `SoakReport`s + the apples-to-apples latency/cost/WER) → `docs/COMPARISON_WRITEUP.md` (scaffolded; `[SMOKE: …]` placeholders await real numbers).

## ⭐ (c) CF76 — live cost-wire-shape check (manual, at the run)
Realtime cost is ALREADY token-priced (053-C2b / web §26 — `finalizeTurn` reports `response.done.usage` tokens; the BE's exact-token path prices output). The ONE open verification: does the live GA `response.done.usage` `output_token_details.audio_tokens` field-path match the FE parser (`extractRealtimeUsage`, `realtimeEvents.ts`)? A **small FE parser tweak ONLY if the field path differs**; else realtime cost is correct as-is. _(The brief-092 "undercount" premise was STALE — conflated CF74's duration-fallback with the token path; the FE caught it.)_

## ⭐ Owed cross-doc rows (this seal DEFERRED them to your next /orchestrate-end — itemized so you execute without rediscovery)
Additive, low-drift; land at the post-run `/orchestrate-end` (or now if you prefer):
- **ARCH-009 §6 prose:** the dev-only `POST /api/dev/tts` route (086, Development-gated/unmapped-in-Production) + the `/wer` **explicit-reference path** `{reference, hypothesis}` parallel to the default `phraseId` path (090).
- **Appendix A:** `WerRequest` (+`Reference`, `PhraseId` now optional); `TurnViewModel` row (1403) (+`outputAudioTokens?: number`, realtime-only).
- **`web/CLAUDE.md` cross-doc table:** the `TurnViewModel` `outputAudioTokens` field (mirrors the ARCH-007 §4 TS state shape).
- _(Already landed this seal: web §35 + index, server §27 amendment, the ARCH-020 §15 realization note, `docs/g4-harness-design.md`.)_

## ⭐ (d) SEPARATE deferred — do NOT cram into G.5
The predecessor's **Phase-J / 067–077 / 074–077 focused ARCH-doc-sync pass** (the ARCH-002/003 Phase-J revision + the batched realization-note prose) stays its OWN deferred pass in Carry-forward — NOT folded into G.5. Architecture-quality is eval-judged → worth doing before final submission, but it's not the run's or G.5's blocker.

## ⭐ (e) TEAM STATE at handoff
- **Lead:** stays live (drives the run; re-spawns the fresh FE + orch).
- **Orchestrator:** cycling now (this seal → lead verify-dead → re-spawn fresh).
- **Frontend implementer:** cycling now (FE at 66%; ran `/session-end` — its wiring/reachability audit over `web/src/soak/` + the session doc). Lead re-spawns a fresh FE (picks up run-Finding fix slices + G.5 support).
- **Backend implementer:** **ON HOLD (~32%, idle — NO slice this round; NOT cycled).** The realtime cost was already BE-wired (053-C2b) → G.4 had no BE slice. Re-engage only if a run-Finding needs BE work (WS/continuous stability, or the cascade inter-turn-gap **Option-B** — the persistent-stream continuous-orchestrator rewrite, still smoke-gated).
- **Push:** none configured (no remote) — the round commit is local-only.

## ⚠️ Process notes for the successor
- **Verify-impl-reports discipline HELD — keep it.** Every load-bearing claim independently grep/rev-parse/suite-verified; zero bad commits. **⭐ It also works in REVERSE:** the FE caught the orch's STALE brief-092 premise (realtime cost already done) with file:line evidence before writing no-op tests — the orch verified + deferred to the evidence + corrected the lead/user. **Lesson for the orch: verify a brief's "X is the only gap" premise against the ACTUAL code on BOTH sides, not a stale carry-forward + inference.**
- **Honest-degrade discipline shaped two TWEAKs:** `overlapMeasured` (disclose when overlap is unmeasurable, not a silent "no overlaps") + per-mode `overlapBasis` (realtime token-derived precise vs cascade char-estimate rougher — don't present an estimate as exact). Keep this posture for the run's `SoakReport` interpretation + G.5.
- **`.claude/commands/tdd.md` is modified + uncommitted** — the lead's reviewer-disable edit + the FE's `format:check` process tweak (both lead/scaffolding territory; NOT in this round commit).
- **Cross-bleed:** only `boostlingo-main-` peers are this team; `mvp-*` registry entries are a different project — ignore.
