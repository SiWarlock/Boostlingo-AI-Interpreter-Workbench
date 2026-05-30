import { deriveTurnMetrics } from '../state/selectors'
import { useSessionState } from '../state/sessionStore'
import type { ModeSummary, TurnViewModel } from '../types/domain'

// Latency panel (ARCH-007 / ARCH-013). Three sources, kept distinct (the D.6 load-bearing model):
//   1. Current-turn TOP-LEVEL deltas  -> deriveTurnMetrics (frontend-computed, absolute timestamps).
//   2. Current-turn PER-STAGE         -> the store's `stages` map (server relativeMs, passed through).
//   3. SESSION AVERAGES by mode       -> the backend GET /summary (canonical), held on state.summary.
// Every unavailable metric renders the shared 'n/a' token — never 0, never an error (nice-tier rule).
// Manual-smoke render; the pure derivation is unit-tested in selectors.test.ts.

const NA = 'n/a'

function ms(value: number | null | undefined): string {
  return value === null || value === undefined ? NA : `${Math.round(value)} ms`
}

function ModeAverages({ label, mode }: { label: string; mode?: ModeSummary | null }) {
  const key = label.toLowerCase()
  if (!mode) {
    return (
      <p aria-label={`${key}-averages`}>
        {label}: {NA}
      </p>
    )
  }
  return (
    <div aria-label={`${key}-averages`}>
      <h4>
        {label} ({mode.turnCount} turn{mode.turnCount === 1 ? '' : 's'})
      </h4>
      <ul>
        <li>Avg speech-end → first audio: {ms(mode.avgSpeechEndToFirstAudioMs)}</li>
        <li>Avg speech-end → playback: {ms(mode.avgSpeechEndToPlaybackMs)}</li>
        <li>Avg STT final: {ms(mode.avgSttFinalMs)}</li>
        <li>Avg translation final: {ms(mode.avgTranslationFinalMs)}</li>
        <li>Avg TTS first audio: {ms(mode.avgTtsFirstAudioMs)}</li>
        <li>Errors: {mode.errorCount}</li>
      </ul>
    </div>
  )
}

export default function MetricsPanel({ onRefresh }: { onRefresh?: () => void }) {
  const state = useSessionState()
  const turn: TurnViewModel | undefined = state.currentTurn ?? state.turns[state.turns.length - 1]
  const metrics = turn ? deriveTurnMetrics(turn) : undefined
  const stages = metrics?.stages ?? {}
  const stageNames = Object.keys(stages)
  const summary = state.summary

  return (
    <section aria-label="metrics">
      <h2>Latency</h2>

      <div aria-label="turn-top-level">
        <h3>Current turn</h3>
        <ul>
          <li>Speech-end → first audio: {ms(metrics?.speechEndToFirstAudioMs)}</li>
          <li>Speech-end → playback: {ms(metrics?.speechEndToPlaybackMs)}</li>
          <li>Total turn: {ms(metrics?.totalTurnMs)}</li>
        </ul>
      </div>

      <div aria-label="turn-stages">
        <h3>Cascade stages</h3>
        {stageNames.length === 0 ? (
          <p>{NA}</p>
        ) : (
          <ul>
            {stageNames.map((name) => (
              <li key={name}>
                {name}: {ms(stages[name])}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div aria-label="session-averages">
        <h3>Session averages</h3>
        {onRefresh && (
          <button type="button" onClick={onRefresh}>
            Refresh summary
          </button>
        )}
        <ModeAverages label="Cascade" mode={summary?.cascade} />
        <ModeAverages label="Realtime" mode={summary?.realtime} />
      </div>
    </section>
  )
}
