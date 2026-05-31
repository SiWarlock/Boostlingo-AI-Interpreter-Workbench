import { useEffect, useState } from 'react'
import { Columns2 } from 'lucide-react'
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
//
// H.1 styling (Q2=b): blue-Realtime / violet-Cascade mode-identity cards in the full-width band + the
// .cmp-table treatment on the naturally-tabular cost-by-variant block. CSS/markup only — the per-mode
// metric lines stay single <p> elements (combined "Label: value" text) so every ComparisonSummary.test
// assertion holds unchanged; the cost-by-variant values move to one-per-cell (still substring-matchable).

// A latency value renders as "<n> ms" or "n/a" — NEVER a fabricated 0 for a null/absent measurement.
function formatMs(value: number | null | undefined): string {
  return value === null || value === undefined ? 'n/a' : `${Math.round(value)} ms`
}

function ModeColumn({
  label,
  color,
  mode,
}: {
  label: string
  color: 'blue' | 'violet'
  mode: ModeSummary | null | undefined
}) {
  return (
    <div className={`cmp-mode-card ${color}`} aria-label={`${label}-summary`}>
      <div className="cmp-mode-hd">
        <span className="k" />
        {label}
      </div>
      {!mode ? (
        <p className="cmp-empty-mode">No turns in this mode.</p>
      ) : (
        <>
          <p className="cmp-line">{`Turns: ${mode.turnCount}`}</p>
          <p className="cmp-line">{`Errors: ${mode.errorCount}`}</p>
          <p className="cmp-line">{`Cost/min: ${formatUsdPerMinute(mode.estimatedCostPerMinuteUsd)}`}</p>
          <p className="cmp-line">{`Speech→first audio: ${formatMs(mode.avgSpeechEndToFirstAudioMs)}`}</p>
          <p className="cmp-line">{`Speech→playback: ${formatMs(mode.avgSpeechEndToPlaybackMs)}`}</p>
          <p className="cmp-line">{`STT final: ${formatMs(mode.avgSttFinalMs)}`}</p>
          <p className="cmp-line">{`Translation final: ${formatMs(mode.avgTranslationFinalMs)}`}</p>
          <p className="cmp-line">{`TTS first audio: ${formatMs(mode.avgTtsFirstAudioMs)}`}</p>
        </>
      )}
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
      <section className="cmp-card card-pad" aria-label="comparison-summary">
        <div className="card-hd">
          <span className="ic">
            <Columns2 size={18} aria-hidden />
          </span>
          <span className="card-title">Comparison</span>
        </div>
        <p className="bl-sm">Run some turns to compare Realtime vs Cascade.</p>
      </section>
    )
  }

  const { summary, byVariant } = data

  return (
    <section className="cmp-card card-pad" aria-label="comparison-summary">
      <div className="card-hd">
        <span className="ic">
          <Columns2 size={18} aria-hidden />
        </span>
        <span className="card-title">Comparison — Realtime vs Cascade</span>
        <span className="right eyebrow">{`Total turns: ${summary.turnCount}`}</span>
      </div>

      <div className="cmp-grid">
        <ModeColumn label="Realtime" color="blue" mode={summary.realtime} />
        <ModeColumn label="Cascade" color="violet" mode={summary.cascade} />
      </div>

      <div aria-label="wer-summary" style={{ marginTop: 14 }}>
        {summary.wer ? (
          // WER is unbounded (ARCH-015) — never clamp past 100%; a >1.0 avg is a real signal.
          <p className="cmp-line">{`Avg WER: ${(summary.wer.avgWer * 100).toFixed(1)}% (${summary.wer.sampleCount} sample${
            summary.wer.sampleCount === 1 ? '' : 's'
          })`}</p>
        ) : (
          <p className="cmp-line">Avg WER: n/a</p>
        )}
      </div>

      <div aria-label="cost-by-variant" style={{ marginTop: 14 }}>
        <div className="eyebrow" style={{ marginBottom: 8 }}>
          Estimated cost/min by model variant
        </div>
        {byVariant === null ? (
          <p className="bl-sm">Per-variant cost unavailable.</p>
        ) : byVariant.length === 0 ? (
          <p className="bl-sm">No priced turns yet.</p>
        ) : (
          <table className="cmp-table">
            <thead>
              <tr>
                <th>Mode · model</th>
                <th>Cost / min</th>
                <th>Turns</th>
              </tr>
            </thead>
            <tbody>
              {byVariant.map((v) => (
                <tr key={`${v.mode}-${v.model}`}>
                  <td>{`${v.mode} · ${v.model}`}</td>
                  <td>{formatUsdPerMinute(v.avgCostPerMinuteUsd)}</td>
                  <td>{`${v.turnCount} turn${v.turnCount === 1 ? '' : 's'}`}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  )
}
