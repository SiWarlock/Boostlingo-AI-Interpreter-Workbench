# Session 030 — Orchestrator: live soak run interpreted → G.5 + G.3 → 3 live-test Findings fixed (round seal)

- **Date:** 2026-06-01
- **Role:** orchestrator (boostlingo-main)
- **Predecessor:** orch handoff `027` (wholesale close-out; live run PENDING). **Successor:** the lead's `/team-end` handoff (this round's wrap).
- **Trigger:** post-restart resume → the live run executed → G.5/G.3 + a burst of three user live-test Findings → user-directed close-out.

## What landed (orchestrator framing)

The live real-key 5-min × both-modes soak finally ran (lead-driven). Its persisted SoakReports + session JSON were the inputs to everything below. Six slices + the two G-artifacts + a corrected re-run, all sealed this round.

### Cost-accuracy arc (094/095 + corrected re-run)
- Interpreting the soak surfaced a **realtime cached-audio cost over-count** (~1.5× on context-heavy turns): the estimator priced cached input-audio at the full $32/M rate then added cached on top; the soak showed cached ≈75% of input as the model re-bills the accumulating conversation context each turn. **094** (BE `2ac8adb`) re-prices the cached-audio SUBSET at the $0.40/M cached rate removed from the full-rate base; **095** (FE `5237820`) forwards `cached_tokens_details.audio_tokens` (not the aggregate). The lead's corrected re-run confirmed realtime ~$0.24/min. **CF76 closed — no-op** (the FE parser already read the live field path).
- **The load-bearing comparison insight:** realtime cost CLIMBS with conversation length (stateful context re-billing); cascade is FLAT (stateless). Robust regardless of the cached-pricing fix.

### G.5 + G.3
- **G.5 `docs/COMPARISON_WRITEUP.md` COMPLETE** — all `[SMOKE]`/`[PENDING]` placeholders filled from measured data: realtime ~$0.24/min vs cascade $0.012/min (~20×, widening with session length); ~669 ms vs ~1914 ms first-audio (~2.9×); WER 0.025 vs 0.174 (synthetic-audio caveat); stability all-green. Honesty caveats preserved (cross-run cost not a controlled A/B; barge-in interrupt; overlap-basis asymmetry; realtime WER alignment artifact excluded — used the first run's WER).
- **G.3 `docs/DEMO_SCRIPT.md`** — authored, then revised at user request to a ~6-min presenter-ready SAY/SHOW spoken script. **Kept LOCAL-ONLY** per user (untracked via `.gitignore`; `git rm --cached` at this seal; content stays on disk).

### Three user live-test Findings — all CLOSED, all diagnosed against the REAL session JSON (lesson §34)
- **Finding A** (realtime cost not displaying) → **097 `8b2e555`**. Diagnosed NOT to be the just-shipped 094/095 (definitive mechanism) but **§39/083**: `IsEmptySilence (Completed && 0 transcripts)` excluded every realtime turn (which persists 0 transcripts FE-store-side but a real cost). Fix: `&& CostEstimate is null` (cost-based discriminator, corpus-validated 100%).
- **Finding B** (interpreter ANSWERED instead of translating, in the wrong language, with meta-commentary) → **098 `8d6b25e`**. A gentle append wouldn't hold (the mild instruction was already violated); restructured both realtime templates (conduit framing + target-language lock + question-handling + example). The instruction string is the deterministic/test-pinned deliverable; **effectiveness is eval-observed** — **USER-LIVE-CONFIRMED** "What is your name?" → "¿Cómo te llamas?".
- **Finding C** (cascade per-turn cards flip to N/A on a trailing empty auto-VAD turn) → **099 `692fea4`**. Diagnosed as a pure FE display-selection bug (backend summary CORRECT; NOT a 094/095/097 regression). Fix: one shared `selectDisplayTurn` skipping trailing empty-silence (FE-display sibling of §39/097; AND-not-OR keeps cost-bearing realtime turns meaningful).

## Decisions made
- Realtime cost: **fix** (option A, user-approved), not disclose-only.
- WER recording UX: **push-to-talk** (option A, user-chosen over fixed-window-countdown / VAD-autostop).
- Finding-A discriminator: **cost-based** (`CostEstimate is null`), not mode-based — semantically precise + corpus-validated.
- Finding C: **unify** via one shared `selectDisplayTurn` (not per-panel whack-a-mole); keep-last-meaningful, not a placeholder.

## Verify-discipline highlights (held all round)
- Every Finding diagnosed against the user's **real persisted JSON** before authoring (§34), not code-only. Finding A's suspected cause (094/095) was disproven with a definitive mechanism; a mid-trace mis-flag (4 apparent "cascade 0-cost" turns were realtime turns in a mode-switch session) was caught + corrected via per-turn `.mode`.
- Every implementer load-bearing claim independently verified before sign-off (predicate reads, removed-symbol greps, template content, commit hashes/parents). The FE's recurring stale-before-hash narration (095, 099) was caught both times; commits always verified correctly placed.

## Decisions explicitly NOT made / deferred
- Realtime transcript persistence (history drill-in empty for realtime) — by-design today; a future wire-path decision (Carry-forward).
- Cascade translation-prompt forbid-answering hardening — 098 was realtime-only (no cascade repro); Carry-forward.
- Soak WER index-alignment robustness — non-blocking; Carry-forward.
- Per-stage cascade cost breakdown — user-undecided. The Phase-J/067–077 ARCH-doc-sync pass — its own focused pass.

## Open follow-ups
- The 3 Carry-forward hardening items above (all → G / opportunistic, last-consumers noted).
- The deferred ARCH-doc-sync pass + per-stage cost breakdown (needs a user decision) + G.6 optional deploy — when work resumes.

## Round seal
- Slices: 094 `2ac8adb` · 095 `5237820` · 096 `ab93fbd` · 097 `8b2e555` · 098 `8d6b25e` · 099 `692fea4`. Session docs `028` (BE) / `029` (FE). Suites green at land: server 494 / web 406.
- This `/orchestrate-end` round commit lands the doc reconcile (Log + banner + Carry-forward), the in-tree hot-routing (G.5, lessons, cross-doc, ARCH realization notes), briefs `094`–`099`, this doc, the `docs/runbooks/local-dev-and-test-runbook.md` add, and the `docs/DEMO_SCRIPT.md` untrack. Local-only; no remote.
