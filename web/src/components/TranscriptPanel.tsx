import { Activity, Mic } from 'lucide-react'
import { useSessionState } from '../state/sessionStore'
import type { TurnViewModel } from '../types/domain'
import StatusPill from './StatusPill'
import TurnCard from './TurnCard'

// Live source + target transcripts (ARCH-007 / ARCH-011) rendered as a single CHRONOLOGICAL STREAM of turn
// cards (Phase J / J.4, decision c · Option A): each turn renders a TurnCard (a direction badge + the
// side-by-side source/target), oldest→newest with the in-progress turn last. Applies to ALL sessions — a
// one-direction session shows a constant badge, a bidirectional session shows it alternate per turn. The
// store normalizes partials→finals into {text,isFinal}[] (lesson §10); this only projects (ARCH-007).
// Styling (the card stream / scroll) is manual-smoke; the structure + per-turn badge are unit-tested.

export default function TranscriptPanel() {
  const state = useSessionState()
  // The chronological stream: completed turns (oldest→newest) then the in-progress turn (newest), if any.
  const turns: TurnViewModel[] = [...state.turns, state.currentTurn].filter(
    (t): t is TurnViewModel => t !== undefined,
  )

  return (
    <section className="card card-pad" aria-label="transcripts" style={{ minHeight: 360 }}>
      <div className="card-hd">
        <span className="ic">
          <Activity size={18} aria-hidden />
        </span>
        <span className="card-title">Live transcript</span>
        {turns.length > 0 && (
          <span className="right">
            <StatusPill value={state.turnStatus} />
          </span>
        )}
      </div>

      {turns.length === 0 ? (
        <div className="tx-empty">
          <Mic size={26} strokeWidth={1.5} aria-hidden />
          <div>
            No turn yet — press <b>Start recording</b> to begin.
          </div>
        </div>
      ) : (
        <ol className="tx-stream list-reset" aria-label="transcript-stream">
          {turns.map((turn) => (
            <li key={turn.turnId}>
              <TurnCard turn={turn} />
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}
