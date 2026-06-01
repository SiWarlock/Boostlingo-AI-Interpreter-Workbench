import type { TurnViewModel } from '../types/domain'
import DirectionBadge from './DirectionBadge'

// Phase J / J.4 — one turn's card in the chronological transcript stream (decision c · Option A): a
// direction badge above the turn's side-by-side source/target columns (the workbench original/translation
// comparison preserved). The private TranscriptColumn + the realtime "source unavailable" note (PRD
// must-have 6) moved here verbatim from TranscriptPanel; the store already normalizes partials→finals into
// {text,isFinal}[] (lesson §10), so this only renders. Styling is manual-smoke; structure is unit-tested.

type Segment = { text: string; isFinal: boolean }

function TranscriptColumn({
  label,
  flag,
  accent,
  segments,
}: {
  label: string
  flag: string
  accent: string
  segments: Segment[]
}) {
  return (
    <div className="tx-col">
      <div className="tx-hd">
        <span className="eyebrow">{label}</span>
        <span className="tx-flag">{flag}</span>
      </div>
      <ol className="list-reset" aria-label={`${label}-transcript`}>
        {segments.map((seg, i) => (
          <li
            key={`${label}-${i}`}
            className={`tx-line${seg.isFinal ? '' : ' partial'}`}
            data-final={seg.isFinal}
          >
            {seg.text}
            {!seg.isFinal && <span className="cursor" style={{ background: accent }} />}
          </li>
        ))}
      </ol>
    </div>
  )
}

export default function TurnCard({ turn }: { turn: TurnViewModel }) {
  const { direction, sourceTranscript: source, targetTranscript: target } = turn
  // Realtime-only: if a realtime turn produced no source transcript, say so explicitly (PRD must-have 6 —
  // never silently show only the target). Cascade never hits this branch.
  const sourceUnavailable = turn.mode === 'realtime' && source.length === 0

  return (
    <div className="tx-card" aria-label="turn-card">
      <div className="tx-card-hd">
        <DirectionBadge direction={direction} />
      </div>
      <div className="tx-cols">
        {sourceUnavailable ? (
          <div className="tx-col">
            <div className="tx-hd">
              <span className="eyebrow">source</span>
              <span className="tx-flag">{direction.source.toUpperCase()}</span>
            </div>
            <p aria-label="source-unavailable" className="tx-line partial">
              Source transcript unavailable for this mode.
            </p>
          </div>
        ) : (
          <TranscriptColumn
            label="source"
            flag={direction.source.toUpperCase()}
            accent="var(--bl-blue)"
            segments={source}
          />
        )}
        <TranscriptColumn
          label="target"
          flag={direction.target.toUpperCase()}
          accent="var(--bl-violet)"
          segments={target}
        />
      </div>
    </div>
  )
}
