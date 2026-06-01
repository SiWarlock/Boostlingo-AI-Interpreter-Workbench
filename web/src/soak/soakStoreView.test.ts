import { describe, expect, it } from 'vitest'
import { createSoakStoreView } from './soakStoreView'
import type { LatencyEvent, TurnViewModel } from '../types/domain'

// The store→SoakTurnObservation adapter (089b) — the production side of the 089a `SoakStoreView` seam.
// Reads the real store `turns[]` (no bypass, ARCH-007) and projects each to the soak's observation:
// end-to-end latency from deriveTurnMetrics (the ARCH-013 responsiveness headline), the joined final source
// transcript (the WER hypothesis), and the run-relative playback END (start + output duration, NOT start).

function ev(name: string, isoTimestamp: string): LatencyEvent {
  return {
    name,
    stage: 'overall',
    timestamp: isoTimestamp,
    relativeMs: 0,
    clockSource: 'browser',
    metadata: {},
  }
}

function turn(over: Partial<TurnViewModel>): TurnViewModel {
  return {
    turnId: 't',
    mode: 'cascade',
    direction: { source: 'en', target: 'es' },
    status: 'completed',
    startedAt: '2026-01-01T00:00:00.000Z',
    sourceTranscript: [],
    targetTranscript: [],
    latency: {},
    errors: [],
    ...over,
  }
}

describe('createSoakStoreView', () => {
  it('maps store turns to ordered observations (latency via deriveTurnMetrics, transcript joined)', () => {
    const turns: TurnViewModel[] = [
      turn({
        turnId: 't0',
        sourceTranscript: [{ text: 'hello', isFinal: true }],
        latencyEvents: [
          ev('stt.final', '2026-01-01T00:00:01.000Z'),
          ev('tts.first_audio', '2026-01-01T00:00:02.000Z'),
        ],
      }),
      turn({ turnId: 't1', sourceTranscript: [{ text: 'hola', isFinal: true }] }),
    ]
    const view = createSoakStoreView({
      getTurns: () => turns,
      runStartMs: 0,
      resolveOutputDurationMs: () => null,
    })

    const obs = view.getCompletedTurns()
    expect(obs).toHaveLength(2)
    expect(obs[0].index).toBe(0)
    expect(obs[0].sourceTranscript).toBe('hello')
    expect(obs[0].endToEndLatencyMs).toBe(1000) // stt.final → tts.first_audio = 1s
    expect(obs[1].index).toBe(1)
    expect(obs[1].sourceTranscript).toBe('hola')
    expect(obs[1].endToEndLatencyMs).toBeNull() // no events → honest null (skipped from the slope)
  })

  it('derives playbackEndMs as playback-START (run-relative) PLUS the output duration, not the start', () => {
    const runStartMs = Date.parse('2026-01-01T00:00:00.000Z')
    const turns = [turn({ latencyEvents: [ev('playback.started', '2026-01-01T00:00:05.000Z')] })]

    const withDuration = createSoakStoreView({
      getTurns: () => turns,
      runStartMs,
      resolveOutputDurationMs: () => 2000,
    })
    // playback.started at +5000ms run-relative; + 2000ms output → END 7000, NOT the 5000 start.
    expect(withDuration.getCompletedTurns()[0].playbackEndMs).toBe(7000)

    // No reliable output duration → honest null (the overlap detector skips that pair, per 087).
    const noDuration = createSoakStoreView({
      getTurns: () => turns,
      runStartMs,
      resolveOutputDurationMs: () => null,
    })
    expect(noDuration.getCompletedTurns()[0].playbackEndMs).toBeNull()
  })
})
