import { Gauge, RefreshCw } from 'lucide-react'
import { deriveTurnMetrics } from '../state/selectors'
import { useSessionState } from '../state/sessionStore'
import type { InterpretationMode, ModeSummary, TurnViewModel } from '../types/domain'
import { latencyCeilingMs, latencyTier } from './latencyTarget'

// Latency panel (ARCH-007 / ARCH-013). Three sources, kept distinct (the D.6 load-bearing model):
//   1. Current-turn TOP-LEVEL deltas  -> deriveTurnMetrics (frontend-computed, absolute timestamps).
//   2. Current-turn PER-STAGE         -> the store's `stages` map (server relativeMs, passed through).
//   3. SESSION AVERAGES by mode       -> the backend GET /summary (canonical), held on state.summary.
// Every unavailable metric renders the shared 'n/a' token — never 0, never an error (nice-tier rule).
// Manual-smoke render; the pure derivation is unit-tested in selectors.test.ts.
//
// H.1 styling: card + a big-mono headline (latency-vs-target colored via the pure latencyTier helper) +
// a per-stage segmented bar. CSS/markup only — deriveTurnMetrics is kept (NOT swapped for a direct
// latency.X read); the turn-top-level / turn-stages / session-averages aria-labels are unchanged.

const NA = 'n/a'

function ms(value: number | null | undefined): string {
  return value === null || value === undefined ? NA : `${Math.round(value)} ms`
}

const TIER_CLASS = { good: 'good', warn: 'warn', over: 'over', na: '' } as const

const STAGE_META: { key: string; label: string; color: string }[] = [
  { key: 'stt', label: 'STT', color: 'var(--bl-blue)' },
  { key: 'translation', label: 'Translation', color: 'var(--bl-violet)' },
  { key: 'tts', label: 'TTS', color: 'var(--bl-coral)' },
]

function Kv({ k, v }: { k: string; v: string }) {
  return (
    <div className="kv">
      <span className="k">{k}</span>
      <span className="v">{v}</span>
    </div>
  )
}

function ModeAverages({ label, mode }: { label: string; mode?: ModeSummary | null }) {
  const key = label.toLowerCase()
  if (!mode) {
    return (
      <p aria-label={`${key}-averages`} className="na" style={{ marginTop: 6 }}>
        {label}: {NA}
      </p>
    )
  }
  // Realtime is a SINGLE model — it has no discrete STT/Translation/TTS stages, so those three averages are
  // always n/a for it. Render ONE explanatory note in their place instead of three n/a rows (which mis-read
  // as "missing data" rather than "doesn't apply" — ARCH-013, 074). Cascade keeps its per-stage averages.
  // The responsiveness (speech-end → first audio / → playback) + Errors rows apply to BOTH modes.
  const isRealtime = key === 'realtime'
  return (
    <div aria-label={`${key}-averages`} style={{ marginTop: 6 }}>
      <div className="eyebrow" style={{ marginBottom: 2 }}>
        {label} ({mode.turnCount} turn{mode.turnCount === 1 ? '' : 's'})
      </div>
      <Kv k="Avg speech-end → first audio" v={ms(mode.avgSpeechEndToFirstAudioMs)} />
      <Kv k="Avg speech-end → playback" v={ms(mode.avgSpeechEndToPlaybackMs)} />
      {isRealtime ? (
        <p className="na" style={{ margin: '2px 0' }}>
          Single model — no discrete stages
        </p>
      ) : (
        <>
          <Kv k="Avg STT final" v={ms(mode.avgSttFinalMs)} />
          <Kv k="Avg translation final" v={ms(mode.avgTranslationFinalMs)} />
          <Kv k="Avg TTS first audio" v={ms(mode.avgTtsFirstAudioMs)} />
        </>
      )}
      <Kv k="Errors" v={String(mode.errorCount)} />
    </div>
  )
}

export default function MetricsPanel({ onRefresh }: { onRefresh?: () => void }) {
  const state = useSessionState()
  const turn: TurnViewModel | undefined = state.currentTurn ?? state.turns[state.turns.length - 1]
  const metrics = turn ? deriveTurnMetrics(turn) : undefined
  const stages = metrics?.stages ?? {}
  const summary = state.summary

  const mode: InterpretationMode = turn?.mode ?? state.mode
  // Headline = the RESPONSIVENESS metric (speech-end → first audio) for BOTH modes — the <3s/<1.5s
  // ARCH-013 targets are responsiveness targets, never total-turn (which includes the user's talk + hold).
  // Total-turn is shown as secondary context in the per-turn rows, with NO badge (056 bug 4). Cascade's
  // speech-end is stt.final-anchored (deriveTurnMetrics) so it's hold-robust + positive.
  const headlineMs = metrics?.speechEndToFirstAudioMs
  const tier = latencyTier(mode, headlineMs)
  const ceilingS = latencyCeilingMs(mode) / 1000

  return (
    <section className="card card-pad" aria-label="metrics">
      <div className="card-hd">
        <span className="ic">
          <Gauge size={18} aria-hidden />
        </span>
        <span className="card-title">Latency</span>
        {onRefresh && (
          <span className="right">
            <button type="button" className="btn btn-ghost btn-sm" onClick={onRefresh}>
              <span className="ic">
                <RefreshCw size={15} aria-hidden />
              </span>
              Refresh summary
            </button>
          </span>
        )}
      </div>

      <div className="eyebrow">This turn · speech → first audio</div>
      <div
        aria-label="turn-headline"
        style={{ display: 'flex', alignItems: 'baseline', gap: 10, marginTop: 8, flexWrap: 'wrap' }}
      >
        <span className={`metric-big ${TIER_CLASS[tier]}`}>
          {headlineMs === null || headlineMs === undefined ? (
            <span className="na">{NA}</span>
          ) : (
            ms(headlineMs)
          )}
        </span>
        {tier !== 'na' && <span className={`tgt-pill tgt-${tier}`}>target &lt; {ceilingS}s</span>}
      </div>

      {/* Per-stage is cascade-only (ARCH-013): realtime has no STT/Translation/TTS stages, so the whole
          section is mode-gated — a hardcoded "Cascade stages" header is wrong for realtime (Fix B, brief
          049). The values are deriveTurnMetrics's stt/translation/tts stage DURATIONS (ARCH-013, 056). */}
      {mode === 'cascade' && (
        <div aria-label="turn-stages" style={{ marginTop: 16 }}>
          <div className="eyebrow">Cascade stages</div>
          <div className="stage-bar">
            {STAGE_META.map((st) => {
              const v = stages[st.key]
              const total = STAGE_META.reduce((a, x) => a + Math.max(0, stages[x.key] ?? 0), 0) || 1
              return v ? (
                <div
                  key={st.key}
                  className="seg-fill"
                  style={{ width: `${(v / total) * 100}%`, background: st.color }}
                />
              ) : null
            })}
          </div>
          <div className="stage-legend">
            {STAGE_META.map((st) => (
              <span key={st.key} className="lg">
                <span className="k" style={{ background: st.color }} />
                {st.label} {ms(stages[st.key])}
              </span>
            ))}
          </div>
        </div>
      )}

      <div className="divider" />

      <div aria-label="turn-top-level">
        <div className="eyebrow" style={{ marginBottom: 4 }}>
          Current turn
        </div>
        <Kv k="Speech-end → first audio" v={ms(metrics?.speechEndToFirstAudioMs)} />
        <Kv k="Speech-end → playback" v={ms(metrics?.speechEndToPlaybackMs)} />
        <Kv k="Total turn" v={ms(metrics?.totalTurnMs)} />
      </div>

      <div aria-label="session-averages">
        <div className="divider" />
        <div className="eyebrow">Session averages</div>
        <ModeAverages label="Cascade" mode={summary?.cascade} />
        <ModeAverages label="Realtime" mode={summary?.realtime} />
      </div>
    </section>
  )
}
