# /tdd brief — frontend_transcript_bidirectional_ux

## Feature
Render the transcript as a **single chronological stream of turn cards**, each with a per-turn **direction badge** ("EN→ES" / "ES→EN" arrow) and the turn's source + target inside the card (preserving the workbench's side-by-side original/translation). Applies to all sessions (one-direction shows a constant badge; bidirectional shows alternating badges). Transcript-UX decision (c) → Option A.

## Use case + traceability
- **Task ID:** J.4 (Phase J — Bidirectional)
- **Architecture sections it implements:** ARCH-007 (transcript display), ARCH-011 (cascade transcripts), ARCH-013 (per-turn metrics context)
- **Related context:** `docs/bidirectional-phase-design.md` (decision c — Option A). Consumes the now-correct per-turn `turn.direction` produced by the sibling wiring brief 080 (cascade `{type:"direction"}` message + realtime heuristic). THIS brief is presentation only.

## Acceptance criteria (what "done" means)
- [ ] The transcript renders turns in a single chronological stream (oldest→newest), current/in-progress turn included with its streaming partials.
- [ ] Each turn card shows a direction badge derived from `turn.direction` ("EN→ES" / "ES→EN", or an arrow + the two flags).
- [ ] Each card shows the turn's source AND target text (side-by-side or clearly grouped — the original/translation pair stays visible).
- [ ] Bidirectional sessions render distinct badges per turn as direction alternates; one-direction sessions render a constant badge (no regression in what's shown).
- [ ] Existing partial-streaming behavior preserved within the current turn's card.
- [ ] Vitest units pass; `tsc --noEmit` clean; `/preflight` clean.

## Files expected to touch
**New (likely):**
- `web/src/components/TurnCard.tsx` — a single turn's card (badge + source/target).
- `web/src/components/DirectionBadge.tsx` — the EN→ES / ES→EN badge.
- Their test files.

**Modified:**
- `web/src/components/TranscriptPanel.tsx` — restructure from the fixed two-column source/target layout to the chronological turn-card stream (reuse `TranscriptColumn` inside the card if it fits).
- Any snapshot/render tests for `TranscriptPanel`.

If implementation needs files beyond this list, **flag at Step 2.5**.

## RED test outline (Step 2)
1. **`renders_turns_in_chronological_order`** — given N turns, they render oldest→newest. Why: Option A chronological stream.
2. **`turn_card_shows_direction_badge`** — a turn with `direction {es→en}` renders an "ES→EN" badge. Why: per-turn badge.
3. **`bidirectional_turns_show_alternating_badges`** — turns `[en→es, es→en]` ⇒ two distinct badges. Why: the bidirectional payoff.
4. **`turn_card_shows_source_and_target`** — both the source and target text appear in a card. Why: preserve the original/translation pair.
5. **`current_turn_streams_partials_in_its_card`** — the in-progress turn's partials render within its card. Why: no streaming regression.

## Cross-doc invariant impact
- **Model field changes:** none (consumes existing `TurnViewModel.direction`). Pure presentation — note "no contract change" at Step 9.

## Things to flag at Step 2.5
1. **Source/target layout inside the card** — keep the existing two-column side-by-side, or stack (source above target)? **Default vote: keep side-by-side inside each card** (preserves the workbench comparison); badge sits above. Confirm with the user's taste if a stacked layout reads better for alternating directions.
2. **Apply to ALL sessions or only bidirectional?** **Default vote: ALL** (one consistent layout; one-direction just shows a constant badge) — simpler than maintaining two transcript layouts. If the user prefers the old two-column layout for one-direction, that's a fork — flag it.
3. **Badge form** — "EN→ES" text vs flag-arrow-flag (🇬🇧→🇪🇸) vs both? **Default vote: arrow + uppercase codes ("EN → ES")**, optionally with the existing flag glyphs the `TranscriptColumn` already uses. Keep it legible at a glance.

## Dependencies + sequencing
- **Depends on:** 080 (correct per-turn `turn.direction` for both modes). Lands same round, after 080.
- **Blocks:** nothing (terminal FE slice of Phase J).

## Estimated commit count
**1.** Focused presentation change; no safety invariant, no contract change.

## Lessons-logged candidates anticipated
- **Convention candidate** — chronological turn-card + direction-badge as the transcript layout (replaces the fixed source/target columns) — the bidirectional-ready presentation.
