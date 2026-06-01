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
    config: {
      currentMode: 'cascade',
      direction: { source: 'en', target: 'es' },
      providerProfile: {
        realtimeProvider: 'openai',
        realtimeModel: 'gpt-realtime',
        sttProvider: 'deepgram',
        sttModel: 'nova-3',
        sttLanguage: 'multi',
        translationProvider: 'openai',
        translationModel: 'gpt-5-nano',
        ttsProvider: 'openai',
        ttsModel: 'gpt-4o-mini-tts',
        ttsVoice: 'alloy',
      },
    },
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

    // turn 2 (realtime, failed): its transcript + the failed status. NOTE: exact 'realtime' for the mode chip
    // (the realtime turn now also surfaces the Model "gpt-realtime" from config — J.7/2a — so the substring
    // /realtime/i would match two elements; the assertion that row 1 is the realtime mode is unchanged).
    expect(within(rows[1]).getByText('segunda')).toBeInTheDocument()
    expect(within(rows[1]).getByText('realtime')).toBeInTheDocument()
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

describe('SessionDetail — Phase-J display fixes (J.7 / 2a)', () => {
  function rows(session: InterpretationSession) {
    render(<SessionDetail session={session} />)
    return within(screen.getByLabelText('session-turns')).getAllByRole('listitem')
  }

  it('shows the realtime model from session config for a realtime turn (not n/a)', () => {
    // The realtime turn carries translationModelUsed:null (cascade-only field) → today it shows Model n/a.
    // The model IS knowable from config.providerProfile.realtimeModel ('gpt-realtime'), independent of the turn.
    const r = rows(sessionWith({}))
    expect(within(r[1]).getByText(/gpt-realtime/)).toBeInTheDocument() // row 1 = the realtime turn
  })

  it('shows the cascade translation model (per-turn) and falls back to config when the turn omits it', () => {
    // per-turn translationModelUsed present → shows it
    expect(within(rows(sessionWith({}))[0]).getByText(/gpt-5-nano/)).toBeInTheDocument()
    // per-turn absent → falls back to config.providerProfile.translationModel
    cleanup()
    const turn = { ...cascadeTurn, translationModelUsed: null }
    expect(
      within(rows(sessionWith({ turns: [turn] }))[0]).getByText(/gpt-5-nano/),
    ).toBeInTheDocument()
  })

  it('shows a completed cascade turn its PER-TURN $/min (not the session-average) — verify', () => {
    // The real session JSON corrected the premise: completed cascade turns DO carry a per-turn
    // estimatedUsdPerMinute → display it directly, NOT the session-summary rate. The fixture pins the
    // distinction: per-turn 0.6 vs summary.cascade 0.42. (No completed-turn cost bug → no session fallback.)
    const costKv = within(rows(sessionWith({}))[0]).getByText('Cost').closest('.kv')
    expect(costKv).toHaveTextContent(/0\.60/) // the PER-TURN value
    expect(costKv).not.toHaveTextContent(/0\.42/) // NOT the session-average
  })

  it('keeps an honest n/a for a FAILED cascade turn with null cost (the lead-observed n/a)', () => {
    // turns[3] in the real session = a FAILED cascade turn with costEstimate:null → honest n/a (correct).
    // The cascade Cost=n/a the lead saw was THIS failed turn, not a completed-turn display bug.
    const turn = { ...cascadeTurn, status: 'failed', costEstimate: null }
    const costKv = within(rows(sessionWith({ turns: [turn] }))[0]).getByText('Cost').closest('.kv')
    expect(costKv).toHaveTextContent(/n\/a/i)
  })
})
