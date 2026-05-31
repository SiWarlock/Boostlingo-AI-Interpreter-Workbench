import { describe, expect, it } from 'vitest'
import { toTurnDetailView } from './historyDetail'
import type { InterpretationTurn } from '../types/domain'

// toTurnDetailView is the focused projection (like F.3's ComparisonTurn) over the OPAQUE wire
// InterpretationTurn (Record<string, unknown>) — it reads ONLY the fields the per-turn breakdown renders,
// keeping the opaque-until-consumed posture. The wire shape (068 GET /{id}): turnId, mode, status,
// latencyEvents[], transcripts[], costEstimate, translationModelUsed, werResult, isEvaluation.

const wireTurn = {
  turnId: 'turn_1',
  mode: 'cascade',
  status: 'completed',
  latencyEvents: [
    {
      name: 'stt.final',
      stage: 'stt',
      timestamp: '2026-05-31T10:00:01+00:00',
      relativeMs: 100,
      clockSource: 'server',
      metadata: {},
    },
  ],
  transcripts: [
    {
      segmentId: 's1',
      role: 'source',
      text: 'hello',
      isFinal: true,
      provider: 'deepgram',
      timestamp: '2026-05-31T10:00:00+00:00',
      clockSource: 'server',
    },
  ],
  costEstimate: {
    provider: 'cascade',
    model: 'gpt-5-nano',
    pricingBasis: 'composite',
    estimatedUsd: 0.01,
    estimatedUsdPerMinute: 0.6,
    units: {},
    pricingConfigVersion: 'v',
    assumptions: [],
  },
  translationModelUsed: 'gpt-5-nano',
  werResult: null,
  isEvaluation: false,
} as unknown as InterpretationTurn

describe('toTurnDetailView', () => {
  it('projects the rendered per-turn fields from the opaque wire turn', () => {
    const v = toTurnDetailView(wireTurn)

    expect(v.turnId).toBe('turn_1')
    expect(v.mode).toBe('cascade')
    expect(v.status).toBe('completed')
    expect(v.latencyEvents).toHaveLength(1)
    expect(v.latencyEvents[0].name).toBe('stt.final') // raw LatencyEvent[] → deriveTurnMetrics consumes them
    expect(v.transcripts).toHaveLength(1)
    expect(v.transcripts[0]).toMatchObject({ role: 'source', text: 'hello' })
    expect(v.cost?.model).toBe('gpt-5-nano') // reads the wire `costEstimate` (§21), not the viewmodel `cost`
    expect(v.translationModelUsed).toBe('gpt-5-nano')
    expect(v.isEvaluation).toBe(false)
  })

  it('degrades a null/absent cost + werResult + non-array latencyEvents/transcripts (the opaque-turn guards)', () => {
    const sparse = {
      turnId: 'turn_2',
      mode: 'realtime',
      status: 'failed',
      costEstimate: null,
      // werResult / translationModelUsed / latencyEvents / transcripts ABSENT
    } as unknown as InterpretationTurn

    const v = toTurnDetailView(sparse)

    expect(v.mode).toBe('realtime')
    expect(v.status).toBe('failed')
    expect(v.cost).toBeNull()
    expect(v.werResult).toBeNull()
    expect(v.translationModelUsed).toBeNull()
    expect(v.latencyEvents).toEqual([]) // non-array/absent → [] (deriveTurnMetrics-safe)
    expect(v.transcripts).toEqual([])
  })

  it('reads a present werResult (an evaluation turn)', () => {
    const evalTurn = {
      turnId: 'turn_3',
      mode: 'cascade',
      status: 'completed',
      isEvaluation: true,
      werResult: {
        phraseId: 'p1',
        reference: 'the quick brown fox',
        hypothesis: 'the quick fox',
        normalizedReference: 'the quick brown fox',
        normalizedHypothesis: 'the quick fox',
        substitutions: 0,
        insertions: 0,
        deletions: 1,
        referenceWordCount: 4,
        wer: 0.25,
      },
    } as unknown as InterpretationTurn

    const v = toTurnDetailView(evalTurn)

    expect(v.isEvaluation).toBe(true)
    expect(v.werResult?.wer).toBe(0.25)
  })
})
