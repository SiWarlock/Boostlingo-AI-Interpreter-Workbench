import type { LanguageDirection } from '../types/domain'

// Phase J / J.4 — the per-turn direction badge (decision c · Option A: an arrow + uppercase language
// codes, "EN → ES" / "ES → EN"). Pure presentation over turn.direction; rendered once per TurnCard. A
// one-direction session shows a constant badge; a bidirectional session shows it alternate per turn.
export default function DirectionBadge({ direction }: { direction: LanguageDirection }) {
  return (
    <span className="dir-badge" aria-label="direction-badge">
      {direction.source.toUpperCase()} → {direction.target.toUpperCase()}
    </span>
  )
}
