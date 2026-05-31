import { Activity, Mic } from 'lucide-react'
import { useSessionState } from '../state/sessionStore'
import type { LanguageDirection, TurnViewModel } from '../types/domain'
import StatusPill from './StatusPill'

// Live source + target transcripts (ARCH-007 / ARCH-011). A DUMB projection: the store already
// normalizes partials -> finals into {text,isFinal}[] (lesson §10), so this only renders. Partials are
// shown distinct from finals (muted/italic + a blinking caret) so the user sees text as it is produced.
// The "source unavailable" path exists for later realtime reuse (ARCH-010, when input transcription is
// off) and is never exercised in cascade — cascade always produces a source transcript. Manual-smoke.
//
// H.1 styling: card + EN|ES .tx-cols grid (partial dimmed/italic with a caret → solid final), empty
// state. CSS/markup only — the section/ol aria-labels, the data-final attr, and the source-unavailable
// branch are unchanged.

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

export default function TranscriptPanel() {
  const state = useSessionState()
  // Show the in-flight turn live; fall back to the most recent completed turn between turns.
  const turn: TurnViewModel | undefined = state.currentTurn ?? state.turns[state.turns.length - 1]
  const direction: LanguageDirection = turn?.direction ?? state.direction
  const source = turn?.sourceTranscript ?? []
  const target = turn?.targetTranscript ?? []
  // Realtime-only: if a realtime turn produced no source transcript, say so explicitly (PRD must-have
  // 6 — never silently show only the target). Cascade never hits this branch.
  const sourceUnavailable = turn?.mode === 'realtime' && source.length === 0

  return (
    <section className="card card-pad" aria-label="transcripts" style={{ minHeight: 360 }}>
      <div className="card-hd">
        <span className="ic">
          <Activity size={18} aria-hidden />
        </span>
        <span className="card-title">Live transcript</span>
        {turn && (
          <span className="right">
            <StatusPill value={state.turnStatus} />
          </span>
        )}
      </div>

      {!turn ? (
        <div className="tx-empty">
          <Mic size={26} strokeWidth={1.5} aria-hidden />
          <div>
            No turn yet — press <b>Start recording</b> to begin.
          </div>
        </div>
      ) : (
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
      )}
    </section>
  )
}
