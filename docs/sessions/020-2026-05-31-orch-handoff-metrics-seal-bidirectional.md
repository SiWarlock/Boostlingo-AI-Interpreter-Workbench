# Session 020 — Orchestrator handoff: metrics/cost round seal + BIDIRECTIONAL phase framing

- **Date:** 2026-05-31
- **Role:** orchestrator (boostlingo-main) — round close-out + WHOLESALE team cycle
- **Type:** orchestrator handoff doc (the fresh orchestrator reads this on `/orchestrate-start`, alongside `MVP_TASKS.md` "Currently in progress")
- **Predecessor orch handoff:** the 073 seal commit body `9201cb6`
- **Successor:** the fresh orchestrator (re-spawned by the lead after this seal) — **designs the bidirectional phase from this doc**

> Why this doc exists: the user requested a wholesale closeout — fresh orchestrator + both implementers into the big bidirectional phase (lead stays live). This handoff carries the **chat-only context the successor can't derive from the repo**: the bidirectional feature framing + the 3 load-bearing sub-decisions to surface to the lead (→ user) BEFORE any dispatch.

## Round just sealed — metrics/cost-accuracy (074–077) + 071 reconcile

All independently hash-verified (the orchestrator grep/rev-parse-verified every commit — see the process note below):
- **074** `6892430` — sub-cent cost precision (`formatUsdPerMinute` `toFixed(4)` on the sub-dime `0<v<0.10` branch; `!Number.isFinite`→`n/a` closes a latent `$NaN/min`) + realtime stage relabel (`MetricsPanel` `ModeAverages` → "Single model — no discrete stages" note vs three `n/a` rows).
- **075** `8dee6d7` — cascade `tts.started` anchored at TTS request-INITIATION (not the provider's first event) → `TtsFirstAudioMs` is a real to-first-audio latency (was ≈0). **Supersedes the provisional 057c "no fix" decision**; security-reviewer-clean (honest instrumentation, not synthesis). web lesson n/a; **server lesson §37**.
- **076** `f85781a` — realtime `$/min`: `finalizeTurn` sends the turn's recording (source-speech) duration as `audioDurationMs` at `/complete` → the backend's existing `Build` divides → `estimatedUsdPerMinute`. **Lead-confirmed denominator = source-speech minutes (cascade-consistent)**; input+output rejected (~2× headline skew). Disclosure in CostPanel + ComparisonSummary. **web lesson §31**.
- **077** `271adea` — auto-derive a blank session label from the first source-final utterance (≤40-char snippet) with a mode+direction fallback; applied in `SessionService.EndAsync` + `SessionStore.SetLabel` (in-memory/persist agreement). No FE change.
- **071** `b6d1e79` — the previously-unsealed H.3 drill-in (bounded scroll + click-to-expand) — reconciled this round.

Suites: backend **439 green**, web **305 green**. Lessons logged: server **§37**, web **§31**. Cross-doc: `CompleteTurnRequest` row +`audioDurationMs` (mirror-reg, no ARCHITECTURE.md change). Session docs: FE **018**, BE **019**.

**⚠️ Seal residual for the successor to verify/finish:** the FE surfaced a **071 `TurnDetailView` cross-doc row** for `web/CLAUDE.md` (the drill-in's per-turn detail projection — 071 was unsealed so its cross-doc row was never written). If this seal commit didn't already add it, the successor adds it. (076's `CompleteTurnRequest` row WAS added this seal.)

## ⭐⭐ THE NEXT MAJOR BUILD — BIDIRECTIONAL interactive feature (load-bearing; user-clarified)

**The decision is MADE — do NOT re-litigate. Design feasibility + surface the 3 sub-decisions below, then dispatch.**

**What the user wants (their words):** "Someone says something in English → it auto-translates real-time to Spanish; the other person responds in Spanish → auto-translates real-time to English; a back-and-forth conversation can happen; it DETECTS when the other person stops talking and knows when to translate."

**The capability** = within ONE live session: **per-utterance auto-detect the spoken LANGUAGE → translate to the OTHER language → VAD-detect speaker-stop to know when to emit.** This is a **FIRST-CLASS LIVE INTERACTIVE dashboard feature**, NOT just test-harness plumbing. The app is **one-direction EN→ES today**; this adds two-way handling as a reusable interactive feature.

**Both modes (the comparison needs parity):**
- **Realtime** (likely the lighter lift): instruct `gpt-realtime` as a BIDIRECTIONAL interpreter ("detect EN vs ES, render in the other; speak only the translation"). Auto-VAD already detects speaker-stop (073 LIVE-confirmed); `gpt-4o-transcribe` transcribes either language.
- **Cascade** (more wiring): nova-3 already runs `language:"multi"` (seen in `session.created`) → detect source language per utterance → route direction dynamically → translate detected→other → **TTS in the TARGET language** (dynamic target-language voice). Building blocks exist.

**The 5-min stability soak-harness CONSUMES the same capability** — a scripted bidirectional EN↔ES conversation, audio synthesized once via the already-integrated OpenAI TTS (reuse the SAME audio for BOTH modes = fair/repeatable), injected PROGRAMMATICALLY into the capture pipeline (NO virtual audio device). Measures disconnection / drift / memory + collects comparison metrics. (PRD G.4 stability.)

### ⭐ The 3 load-bearing sub-decisions — surface to the lead (→ user via AskUserQuestion) BEFORE any dispatch
1. **Cascade language-detect / dynamic-direction approach** — how cascade detects the source language per utterance (nova-3 `multi` gives detection; how is it surfaced + how is direction routed dynamically per utterance, incl. the target-language TTS voice swap).
2. **Full-cascade-bidirectional lift / phasing** — whether full cascade-bidirectional is a big enough lift to warrant a phased rollout or a user scope call (vs. realtime-bidirectional first).
3. **Both-directions transcript-panel UX** — how to show both directions (EN→ES and ES→EN turns) in the transcript panel.

The lead takes these to the user via AskUserQuestion. **No bidirectional dispatch until the sub-decisions settle AND the fresh impls are verified up.**

## Carry-forward (full list in MVP_TASKS Carry-forward; the load-bearing ones)
- **⭐ Bidirectional interactive feature** (above) — the next major build (FE + BE + the soak-harness).
- **Cascade `playback.started` diagnosis** (FE) — the stamp EXISTS (`playbackController` stamps on `playing`); the `speechEndToPlaybackMs` n/a gap is subtler (likely the session-avg reporting path — cascade browser-clock events not reported to the backend — OR auto-VAD turn-attribution). Needs a precise diagnosis (live symptom: per-turn vs session-avg n/a) BEFORE authoring. (Lead's metrics item (d), the missing half.)
- **WER eval fix** — capture-gated (HIGH; the fabricated-100% Finding). Lead coordinates the user-run eval capture → routes to the orch. Don't block on it.
- **ARCH-doc-sync micro-routing** — granular ARCHITECTURE.md realization-note prose deferred (context-budget; honest-residual precedent): ARCH-013/014 (074-076), ARCH-016/017 (077), + the still-pending 067-072 notes. A focused ARCH-doc-sync pass.
- **Transcript-role-constant canonicalization** (BE, LOW/opportunistic) — the `"source"` literal duplicated across `SessionLabelDeriver` + cascade `RoleSource` + FE `realtimeEventSink`.
- **Per-stage cost breakdown** (Cost panel) — user-UNDECIDED; do NOT action unless the lead relays the user's confirmation.

## ⚠️ Process note for the successor — VERIFY IMPLEMENTER REPORTS
The outgoing FE implementer had a recurring **phantom/premature-reporting pattern** (a confabulated nonexistent `MetricsPanel` structure at a Step-2.5; TWO phantom commit hashes `4f8e2a1`/`2a7f1c9` that don't exist in git; a premature Step-9 with 2 silently-failed edits + a fabricated test-mechanics note). **The engineering was consistently SOUND and ZERO bad commits landed — because the orchestrator independently grep/rev-parse-verified every load-bearing claim before approving.** The fresh FE is a clean context, but **keep the discipline: spot-check `finalizeTurn`-style load-bearing impl, `cat-file`/`rev-parse`-verify every reported hash, grep-verify disclosure/copy edits that have no failing test to catch a silent miss.** The BE was reliable throughout.

## Team state at handoff
- **Lead:** stays LIVE (no `/team-end`).
- **Orchestrator:** cycling now (this handoff → lead shutdown → fresh orch reads this + designs bidirectional).
- **Both implementers:** cycled (docs 018/019 committed); the lead re-spawns a fresh FE + fresh BE. The fresh orch dispatches bidirectional briefs to them AFTER the 3 sub-decisions settle.
- **Pointers:** lessons server `LESSONS.md#37` / web `LESSONS.md#31`; cross-doc `web/CLAUDE.md` CompleteTurnRequest row; briefs `074`–`077` in `docs/briefs/`; FE doc `018` / BE doc `019`.
