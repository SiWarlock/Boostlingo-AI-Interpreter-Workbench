// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import SessionDetail from './SessionDetail'
import type { InterpretationSession } from '../types/domain'

// SessionDetail is a read-only renderer for a fetched past session (071 drill-in): the embedded `summary`
// aggregates + a per-turn breakdown over a focused TurnDetailView projection. Pure presentation over static
// data (NOT the live store) — reuses deriveTurnMetrics (§25) + the cost display (§21) + the mode chips.

// Cascade turn with the ARCH-013 stage markers → deriveTurnMetrics derives stt/translation/tts durations.
const cascadeTurn = {
  turnId: 'turn_1',
  mode: 'cascade',
  status: 'completed',
  latencyEvents: [
    mk('cascade.audio.received', '2026-05-31T10:00:00.000+00:00'),
    mk('stt.final', '2026-05-31T10:00:00.120+00:00'),
    mk('translation.started', '2026-05-31T10:00:00.120+00:00'),
    mk('translation.final', '2026-05-31T10:00:00.360+00:00'),
    mk('tts.started', '2026-05-31T10:00:00.400+00:00'),
    mk('tts.complete', '2026-05-31T10:00:00.440+00:00'),
  ],
  transcripts: [seg('source', 'hello world'), seg('target', 'hola mundo')],
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
}

const realtimeTurn = {
  turnId: 'turn_2',
  mode: 'realtime',
  status: 'failed',
  latencyEvents: [],
  transcripts: [seg('target', 'segunda')],
  costEstimate: null,
  translationModelUsed: null,
  werResult: null,
  isEvaluation: false,
}

function mk(name: string, timestamp: string) {
  return { name, stage: 'overall', timestamp, relativeMs: 0, clockSource: 'server', metadata: {} }
}
function seg(role: 'source' | 'target', text: string) {
  return {
    segmentId: `${role}-${text}`,
    role,
    text,
    isFinal: true,
    provider: 'deepgram',
    timestamp: '2026-05-31T10:00:00+00:00',
    clockSource: 'server',
  }
}

function sessionWith(overrides: Partial<InterpretationSession>): InterpretationSession {
  return {
    sessionId: 'session_2',
    label: 'Recent run',
    startedAt: '2026-05-31T10:00:00+00:00',
    endedAt: '2026-05-31T10:05:00+00:00',
    summary: {
      turnCount: 2,
      cascade: {
        turnCount: 1,
        avgSpeechEndToFirstAudioMs: 500,
        avgSpeechEndToPlaybackMs: null,
        estimatedCostPerMinuteUsd: 0.42,
        errorCount: 0,
        avgSttFinalMs: 120,
        avgTranslationFinalMs: 240,
        avgTtsFirstAudioMs: 360,
      },
      realtime: {
        turnCount: 1,
        avgSpeechEndToFirstAudioMs: 300,
        avgSpeechEndToPlaybackMs: null,
        estimatedCostPerMinuteUsd: null,
        errorCount: 1,
      },
      wer: { sampleCount: 1, avgWer: 0.25 },
      computedAt: '2026-05-31T10:05:00+00:00',
      pricingConfigVersion: 'v',
    },
    turns: [cascadeTurn, realtimeTurn],
    modeTransitions: [],
    pricingConfigVersion: 'v',
    ...overrides,
  } as unknown as InterpretationSession
}

afterEach(() => cleanup())

describe('SessionDetail', () => {
  it('renders the summary aggregates (turnCount + cascade/realtime avgs + WER)', () => {
    render(<SessionDetail session={sessionWith({})} />)

    const summary = screen.getByLabelText('session-summary-detail')
    expect(within(summary).getByText(/2 turns/i)).toBeInTheDocument() // turnCount
    expect(within(summary).getByText(/120/)).toBeInTheDocument() // cascade avg STT final
    expect(within(summary).getByText(/25/)).toBeInTheDocument() // WER avg (0.25 → 25%)
  })

  it('renders a per-turn breakdown for each turn (mode/status/transcripts/model/cost), in order', () => {
    render(<SessionDetail session={sessionWith({})} />)

    const turns = screen.getByLabelText('session-turns')
    const rows = within(turns).getAllByRole('listitem')
    expect(rows).toHaveLength(2)

    // turn 1 (cascade, completed): source+target transcript, the model, the cost
    expect(within(rows[0]).getByText('hello world')).toBeInTheDocument()
    expect(within(rows[0]).getByText('hola mundo')).toBeInTheDocument()
    expect(within(rows[0]).getByText(/gpt-5-nano/)).toBeInTheDocument()
    expect(within(rows[0]).getByText(/0\.60/)).toBeInTheDocument() // formatCostPerMinute (§21)
    expect(within(rows[0]).getByText(/cascade/i)).toBeInTheDocument()
    expect(within(rows[0]).getByText(/completed/i)).toBeInTheDocument()
    // a derived per-stage duration renders (deriveTurnMetrics over the markers — §25)
    expect(within(rows[0]).getByText(/120/)).toBeInTheDocument() // STT stage duration

    // turn 2 (realtime, failed): its transcript + the failed status
    expect(within(rows[1]).getByText('segunda')).toBeInTheDocument()
    expect(within(rows[1]).getByText(/realtime/i)).toBeInTheDocument()
    expect(within(rows[1]).getByText(/failed/i)).toBeInTheDocument()
  })

  it('renders a graceful "summary unavailable" note when the session has no summary (not a crash)', () => {
    render(<SessionDetail session={sessionWith({ summary: undefined })} />)

    expect(screen.getByText(/summary unavailable/i)).toBeInTheDocument()
    // the per-turn breakdown still renders
    expect(screen.getByText('hello world')).toBeInTheDocument()
  })

  it('renders a "no turns" note for a turnless session', () => {
    render(<SessionDetail session={sessionWith({ turns: [] })} />)

    expect(screen.getByText(/no turns/i)).toBeInTheDocument()
  })
})
