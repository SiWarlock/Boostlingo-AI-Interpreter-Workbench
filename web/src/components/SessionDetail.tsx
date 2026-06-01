import { deriveTurnMetrics, formatCostPerMinute } from '../state/selectors'
import { toTurnDetailView } from '../state/historyDetail'
import type {
  InterpretationSession,
  LatencyEvent,
  SessionSummary,
  TranscriptSegment,
  TurnViewModel,
} from '../types/domain'

// 071 H.3-frontend drill-in: a READ-ONLY renderer for one fetched past session (the accordion body). Pure
// presentation over STATIC data (NOT the live store, ARCH-007) — it reuses deriveTurnMetrics (§25, per-stage
// durations) + the cost display (§21) + the blue/violet mode chips, the same surfaces the live panels use, so
// the drill-in reads consistently. The embedded `summary` aggregates render above a per-turn breakdown over
// the focused TurnDetailView projection (web §21 — keeps the wire turn opaque).

// `ms` display for a derived duration (absent → n/a, never a fabricated 0 — §13).
function ms(value: number | undefined): string {
  return value === undefined ? 'n/a' : `${Math.round(value)} ms`
}

// Join a role's FINAL segments into one line (a persisted turn carries finalized segments).
function joinRole(transcripts: TranscriptSegment[], role: 'source' | 'target'): string {
  return transcripts
    .filter((s) => s.role === role && s.isFinal)
    .map((s) => s.text)
    .join(' ')
}

// deriveTurnMetrics (§25) reads ONLY turn.latencyEvents — feed it a focused shape (the rest of the
// TurnViewModel is irrelevant to the stage math). Cast through unknown (a partial → the VM type).
function stagesOf(latencyEvents: LatencyEvent[]): Record<string, number> {
  return deriveTurnMetrics({ latencyEvents } as unknown as TurnViewModel).stages ?? {}
}

export default function SessionDetail({ session }: { session: InterpretationSession }) {
  // The GET /{id} payload embeds `summary` (068); the TS InterpretationSession mirror doesn't carry it yet,
  // so read it via a focused cast (the §21 costEstimate precedent — no type graduation this slice).
  const summary = (session as { summary?: SessionSummary }).summary
  const turns = session.turns ?? []

  return (
    <div className="hist-detail" aria-label="session-detail">
      {summary ? (
        <div className="hist-summary" aria-label="session-summary-detail">
          <div className="eyebrow">Session summary</div>
          <div className="kv">
            <span className="k">Turns</span>
            <span className="v">{summary.turnCount} turns</span>
          </div>
          {summary.cascade && (
            <>
              <div className="kv">
                <span className="k">Cascade · avg STT</span>
                <span className="v">{ms(summary.cascade.avgSttFinalMs ?? undefined)}</span>
              </div>
              <div className="kv">
                <span className="k">Cascade · avg translation</span>
                <span className="v">{ms(summary.cascade.avgTranslationFinalMs ?? undefined)}</span>
              </div>
              <div className="kv">
                <span className="k">Cascade · avg TTS first-audio</span>
                <span className="v">{ms(summary.cascade.avgTtsFirstAudioMs ?? undefined)}</span>
              </div>
            </>
          )}
          {summary.realtime && (
            <div className="kv">
              <span className="k">Realtime · avg speech-end → first audio</span>
              <span className="v">
                {ms(summary.realtime.avgSpeechEndToFirstAudioMs ?? undefined)}
              </span>
            </div>
          )}
          {summary.wer && (
            <div className="kv">
              <span className="k">WER</span>
              <span className="v">{(summary.wer.avgWer * 100).toFixed(0)}%</span>
            </div>
          )}
        </div>
      ) : (
        <p className="bl-sm" style={{ margin: '8px 0' }}>
          Summary unavailable for this session.
        </p>
      )}

      {turns.length === 0 ? (
        <p className="bl-sm" style={{ margin: '8px 0' }}>
          No turns in this session.
        </p>
      ) : (
        <ul className="hist-turn-list" aria-label="session-turns">
          {turns.map((wireTurn, i) => {
            const v = toTurnDetailView(wireTurn)
            const stages = stagesOf(v.latencyEvents)
            const source = joinRole(v.transcripts, 'source')
            const target = joinRole(v.transcripts, 'target')
            // J.7/2a Model: read per mode from the authoritative session config (like ComparisonSummary) —
            // realtime → providerProfile.realtimeModel; cascade → the per-turn translationModelUsed, falling
            // back to providerProfile.translationModel (translationModelUsed is cascade-only → null on realtime).
            const model =
              v.mode === 'realtime'
                ? session.config?.providerProfile?.realtimeModel
                : (v.translationModelUsed ?? session.config?.providerProfile?.translationModel)
            return (
              <li className="hist-turn" key={v.turnId || `turn-${i}`}>
                <div className="hist-turn-hd">
                  <span className="chip">
                    <span
                      className="d"
                      style={{
                        background: v.mode === 'realtime' ? 'var(--bl-blue)' : 'var(--bl-violet)',
                      }}
                    />
                    {v.mode}
                  </span>
                  <span className="bl-sm">{v.status}</span>
                </div>
                {source && (
                  <div className="hist-tx">
                    <span className="eyebrow">Source</span>
                    <span className="hist-tx-text">{source}</span>
                  </div>
                )}
                {target && (
                  <div className="hist-tx">
                    <span className="eyebrow">Target</span>
                    <span className="hist-tx-text">{target}</span>
                  </div>
                )}
                <div className="kv">
                  <span className="k">Model</span>
                  <span className="v">{model ?? 'n/a'}</span>
                </div>
                <div className="kv">
                  <span className="k">Cost</span>
                  <span className="v">{formatCostPerMinute(v.cost)}</span>
                </div>
                {stages.stt !== undefined && (
                  <div className="kv">
                    <span className="k">STT</span>
                    <span className="v">{ms(stages.stt)}</span>
                  </div>
                )}
                {stages.translation !== undefined && (
                  <div className="kv">
                    <span className="k">Translation</span>
                    <span className="v">{ms(stages.translation)}</span>
                  </div>
                )}
                {stages.tts !== undefined && (
                  <div className="kv">
                    <span className="k">TTS</span>
                    <span className="v">{ms(stages.tts)}</span>
                  </div>
                )}
                {v.isEvaluation && v.werResult && (
                  <div className="kv">
                    <span className="k">WER</span>
                    <span className="v">{(v.werResult.wer * 100).toFixed(0)}%</span>
                  </div>
                )}
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
