import type { SessionStatus, TurnStatus } from '../types/domain'

// H.1 presentational helper (ARCH-007 — display-only, no store/logic). Renders a session- or turn-status
// value as a color-coded pill with a live indicator (REC pulse / spinner / audio bars), keyed to the
// design tokens in styles/tokens.css. Pure function of its props; never reads the store. Used by the
// Header (sessionStatus), RecordingControls + TranscriptPanel (turnStatus).

type StatusValue = SessionStatus | TurnStatus

// Map each status to the design's `--st-<key>-{bg,fg}` token family. Most map 1:1; a few are remapped
// to the nearest token because the delivered kit's palette predates two states OUR state machine has:
//   - `readyForTurn` -> `ready`      (the kit's own display remap)
//   - `captured`     -> `processing` (a transient post-record/pre-result state; processing-family fits)
//   - `ending`       -> `starting`   (an in-flight session transition; starting/amber-family fits)
// H.2 can introduce dedicated `--st-captured-*` / `--st-ending-*` tokens to refine these deliberately.
const TOKEN_KEY: Record<StatusValue, string> = {
  // sessionStatus
  idle: 'idle',
  configured: 'config',
  starting: 'starting',
  active: 'active',
  readyForTurn: 'ready',
  ending: 'starting',
  ended: 'ended',
  // turnStatus
  ready: 'ready',
  recording: 'recording',
  captured: 'processing',
  processing: 'processing',
  playing: 'playing',
  completed: 'completed',
  failed: 'failed',
}

// A few statuses read better than their raw camelCase enum word (design voice: lowercase, plain).
const LABEL: Partial<Record<StatusValue, string>> = {
  readyForTurn: 'ready',
}

function Indicator({ value }: { value: StatusValue }) {
  if (value === 'recording') return <span className="dot dot-pulse" aria-hidden="true" />
  if (value === 'starting' || value === 'processing' || value === 'captured' || value === 'ending')
    return <span className="spin" aria-hidden="true" />
  if (value === 'playing')
    return (
      <span className="eqbars" aria-hidden="true">
        <i />
        <i />
        <i />
        <i />
      </span>
    )
  return <span className="dot" aria-hidden="true" />
}

export default function StatusPill({
  value,
  large,
  ariaLabel,
}: {
  value: StatusValue
  large?: boolean
  ariaLabel?: string
}) {
  const key = TOKEN_KEY[value]
  const label = LABEL[value] ?? value
  return (
    <span
      className={`pill${large ? ' pill-lg' : ''}`}
      aria-label={ariaLabel}
      style={{ background: `var(--st-${key}-bg)`, color: `var(--st-${key}-fg)` }}
    >
      <Indicator value={value} />
      {label}
    </span>
  )
}
