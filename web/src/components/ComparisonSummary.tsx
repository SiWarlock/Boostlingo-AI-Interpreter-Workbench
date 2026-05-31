import { useEffect, useState } from 'react'
import { sessionsApi } from '../api/sessionsApi'
import { loadComparison } from '../state/comparisonActions'
import type { ComparisonData } from '../state/comparisonActions'
import { formatUsdPerMinute } from '../state/selectors'
import { sessionStore, useSessionState } from '../state/sessionStore'
import type { ModeSummary } from '../types/domain'

// Comparison summary (Flow E, ARCH-009 / ARCH-007). The Realtime-vs-Cascade comparison: per-mode avg
// latency / cost-per-min / errors / turns (from GET /summary) + the per-model-variant cost split
// (derived from GET /session's persisted turns) + WER + total. Renders ONLY from the DI'd loadComparison
// result — no transport internals (clean separation). Honest degradation: a null ModeSummary field is
// "n/a" (never 0); cascade speechEnd→* is n/a (no client→server latency channel, D.5/D.6).

// A latency value renders as "<n> ms" or "n/a" — NEVER a fabricated 0 for a null/absent measurement.
function formatMs(value: number | null | undefined): string {
  return value === null || value === undefined ? 'n/a' : `${Math.round(value)} ms`
}

function ModeColumn({ label, mode }: { label: string; mode: ModeSummary | null | undefined }) {
  if (!mode) {
    return (
      <div aria-label={`${label}-summary`}>
        <h3>{label}</h3>
        <p>No turns in this mode.</p>
      </div>
    )
  }
  return (
    <div aria-label={`${label}-summary`}>
      <h3>{label}</h3>
      <p>{`Turns: ${mode.turnCount}`}</p>
      <p>{`Errors: ${mode.errorCount}`}</p>
      <p>{`Cost/min: ${formatUsdPerMinute(mode.estimatedCostPerMinuteUsd)}`}</p>
      <p>{`Speech→first audio: ${formatMs(mode.avgSpeechEndToFirstAudioMs)}`}</p>
      <p>{`Speech→playback: ${formatMs(mode.avgSpeechEndToPlaybackMs)}`}</p>
      <p>{`STT final: ${formatMs(mode.avgSttFinalMs)}`}</p>
      <p>{`Translation final: ${formatMs(mode.avgTranslationFinalMs)}`}</p>
      <p>{`TTS first audio: ${formatMs(mode.avgTtsFirstAudioMs)}`}</p>
    </div>
  )
}

export default function ComparisonSummary() {
  const state = useSessionState()
  const [data, setData] = useState<ComparisonData | null>(null)
  const sessionId = state.sessionId
  const turnsLen = state.turns.length

  // Load on mount + whenever a turn finalizes (turns grows) — mirrors App's summary-refresh trigger.
  // No session → no data (the empty state). loadComparison routes its own fetch errors to the store.
  useEffect(() => {
    let cancelled = false
    if (sessionId === null) {
      setData(null)
      return
    }
    void loadComparison({ store: sessionStore, api: sessionsApi }).then((result) => {
      if (!cancelled) setData(result)
    })
    return () => {
      cancelled = true
    }
  }, [sessionId, turnsLen])

  if (!data || data.summary.turnCount === 0) {
    return (
      <section aria-label="comparison-summary">
        <h2>Comparison</h2>
        <p>Run some turns to compare Realtime vs Cascade.</p>
      </section>
    )
  }

  const { summary, byVariant } = data

  return (
    <section aria-label="comparison-summary">
      <h2>Comparison — Realtime vs Cascade</h2>
      <p>{`Total turns: ${summary.turnCount}`}</p>

      <div>
        <ModeColumn label="Realtime" mode={summary.realtime} />
        <ModeColumn label="Cascade" mode={summary.cascade} />
      </div>

      <div aria-label="wer-summary">
        {summary.wer ? (
          // WER is unbounded (ARCH-015) — never clamp past 100%; a >1.0 avg is a real signal.
          <p>{`Avg WER: ${(summary.wer.avgWer * 100).toFixed(1)}% (${summary.wer.sampleCount} sample${
            summary.wer.sampleCount === 1 ? '' : 's'
          })`}</p>
        ) : (
          <p>Avg WER: n/a</p>
        )}
      </div>

      <div aria-label="cost-by-variant">
        <h3>Estimated cost/min by model variant</h3>
        {byVariant === null ? (
          <p>Per-variant cost unavailable.</p>
        ) : byVariant.length === 0 ? (
          <p>No priced turns yet.</p>
        ) : (
          <ul>
            {byVariant.map((v) => (
              <li key={`${v.mode}-${v.model}`}>
                {`${v.mode} · ${v.model}: ${formatUsdPerMinute(v.avgCostPerMinuteUsd)} (${
                  v.turnCount
                } turn${v.turnCount === 1 ? '' : 's'})`}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  )
}
