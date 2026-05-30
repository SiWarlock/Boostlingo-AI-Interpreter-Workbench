import { useSessionState } from '../state/sessionStore'
import type { TurnViewModel } from '../types/domain'

// Live source + target transcripts (ARCH-007 / ARCH-011). A DUMB projection: the store already
// normalizes partials -> finals into {text,isFinal}[] (lesson §10), so this only renders. Partials are
// shown distinct from finals (muted/italic + an ellipsis) so the user sees text as it is produced. The
// "source unavailable" path exists for later realtime reuse (ARCH-010, when input transcription is off)
// and is never exercised in cascade — cascade always produces a source transcript. Manual-smoke (D.7
// adds the formal component tests).

type Segment = { text: string; isFinal: boolean }

function TranscriptColumn({ label, segments }: { label: string; segments: Segment[] }) {
  return (
    <div>
      <h3>{label}</h3>
      <ol aria-label={`${label}-transcript`}>
        {segments.map((seg, i) => (
          <li
            key={`${label}-${i}`}
            data-final={seg.isFinal}
            style={seg.isFinal ? undefined : { opacity: 0.6, fontStyle: 'italic' }}
          >
            {seg.text}
            {seg.isFinal ? '' : ' …'}
          </li>
        ))}
      </ol>
    </div>
  )
}

export default function TranscriptPanel() {
  const state = useSessionState()
  // Show the in-flight turn live; fall back to the most recent completed turn between turns.
  const turn: TurnViewModel | undefined = state.currentTurn ?? state.turns[state.turns.length - 1]
  const source = turn?.sourceTranscript ?? []
  const target = turn?.targetTranscript ?? []
  // Realtime-only: if a realtime turn produced no source transcript, say so explicitly (PRD must-have
  // 6 — never silently show only the target). Cascade never hits this branch.
  const sourceUnavailable = turn?.mode === 'realtime' && source.length === 0

  return (
    <section aria-label="transcripts">
      <h2>Transcripts</h2>
      {sourceUnavailable ? (
        <p aria-label="source-unavailable">Source transcript unavailable for this mode.</p>
      ) : (
        <TranscriptColumn label="source" segments={source} />
      )}
      <TranscriptColumn label="target" segments={target} />
    </section>
  )
}
