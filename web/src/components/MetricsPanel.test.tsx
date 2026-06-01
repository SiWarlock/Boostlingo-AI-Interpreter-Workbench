// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import MetricsPanel from './MetricsPanel'
import { sessionStore } from '../state/sessionStore'
import type { LatencyEvent, LatencyStage, SessionSummary } from '../types/domain'

// Fix B (brief 049): the per-stage "Cascade stages" section is cascade-only — realtime has no
// STT/Translation/TTS stages, so a hardcoded "Cascade stages" header under realtime.* events is wrong.
// The section must be mode-gated to cascade (the realtime headline speech→first-audio renders above);
// the cascade `turn-stages` aria-label must be preserved. web §14 (per-file jsdom + cleanup).

afterEach(() => {
  cleanup()
  sessionStore.reset()
})

describe('MetricsPanel — cascade-only per-stage section (Fix B)', () => {
  it('does NOT render the "Cascade stages" section for a realtime turn', () => {
    sessionStore.beginTurn({
      turnId: 't1',
      mode: 'realtime',
      direction: { source: 'en', target: 'es' },
    })

    render(<MetricsPanel />)

    expect(screen.queryByText(/Cascade stages/i)).toBeNull()
    expect(screen.queryByLabelText('turn-stages')).toBeNull()
  })

  it('renders the "Cascade stages" section (turn-stages aria-label preserved) for a cascade turn', () => {
    sessionStore.beginTurn({
      turnId: 't2',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })

    render(<MetricsPanel />)

    expect(screen.getByLabelText('turn-stages')).toBeInTheDocument()
    expect(screen.getByText(/Cascade stages/i)).toBeInTheDocument()
  })
})

describe('MetricsPanel — cascade headline = responsiveness with the target badge (G.4/056 bug 4)', () => {
  it('shows speech-end→first-audio (not total-turn) as the cascade headline + badge; total-turn stays secondary', () => {
    const base = Date.parse('2026-05-31T00:00:00.000Z')
    const at = (ms: number) => new Date(base + ms).toISOString()
    const ev = (name: string, ms: number, stage: LatencyStage = 'overall'): LatencyEvent => ({
      name,
      stage,
      timestamp: at(ms),
      relativeMs: ms,
      clockSource: 'browser',
      metadata: {},
    })
    sessionStore.beginTurn({
      turnId: 't3',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })
    sessionStore.appendLatencyEvent(ev('turn.recording.started', 0))
    sessionStore.appendLatencyEvent(ev('stt.final', 1000, 'stt'))
    sessionStore.appendLatencyEvent(ev('tts.first_audio', 1800, 'tts')) // responsiveness = 800ms (good tier)
    sessionStore.appendLatencyEvent(ev('turn.recording.stopped', 4000)) // manual hold
    sessionStore.appendLatencyEvent(ev('turn.completed', 4100)) // total-turn = 4100ms

    render(<MetricsPanel />)

    // the headline region carries the responsiveness value (800 ms) + the target badge — NOT total-turn
    const headline = screen.getByLabelText('turn-headline')
    expect(headline).toHaveTextContent('800 ms')
    expect(within(headline).getByText(/target <\s*3s/i)).toBeInTheDocument()
    // the cascade eyebrow now frames the headline as speech→first audio (was "total turn")
    expect(screen.getByText(/This turn · speech/i)).toBeInTheDocument()
    // total-turn still shown, as secondary context (in the per-turn rows), no badge
    expect(screen.getByText(/4100 ms/)).toBeInTheDocument()
  })
})

// 074 feature 2: the session-averages ModeAverages renders the 3 stage rows (Avg STT/Translation/TTS final)
// for BOTH modes today — for realtime those are always n/a (realtime is one model, no discrete stages), and
// n/a mis-reads as "missing data." Relabel the realtime block to a "single model — no discrete stages" note
// in place of the 3 stage rows; cascade keeps its stage averages. Query by aria-label/text (web §22/§14).
describe('MetricsPanel — realtime session-averages stage relabel (074)', () => {
  const summary = {
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
    wer: null,
    computedAt: '2026-05-31T10:00:00+00:00',
    pricingConfigVersion: 'v',
  } as unknown as SessionSummary

  it('relabels the realtime block "single model — no discrete stages" instead of three n/a stage rows', () => {
    sessionStore.setSummary(summary)

    render(<MetricsPanel />)

    const block = screen.getByLabelText('realtime-averages')
    // the explanatory note replaces the 3 stage rows (RED: the note is absent today)
    expect(within(block).getByText(/single model.*no discrete stages/i)).toBeInTheDocument()
    // the 3 stage rows are GONE from the realtime block (RED: they currently render as n/a)
    expect(within(block).queryByText('Avg STT final')).toBeNull()
    expect(within(block).queryByText('Avg translation final')).toBeNull()
    expect(within(block).queryByText('Avg TTS first audio')).toBeNull()
    // the rows that DO apply to realtime still render (responsiveness + errors)
    expect(within(block).getByText('Avg speech-end → first audio')).toBeInTheDocument()
    expect(within(block).getByText('Errors')).toBeInTheDocument()
  })

  it('leaves the cascade block UNCHANGED — still shows the three per-stage averages', () => {
    sessionStore.setSummary(summary)

    render(<MetricsPanel />)

    const block = screen.getByLabelText('cascade-averages')
    expect(within(block).getByText('Avg STT final')).toBeInTheDocument()
    expect(within(block).getByText('Avg translation final')).toBeInTheDocument()
    expect(within(block).getByText('Avg TTS first audio')).toBeInTheDocument()
    // and NOT the single-model note (that's realtime-only)
    expect(within(block).queryByText(/single model/i)).toBeNull()
  })
})

// Finding C: after a GOOD turn, a trailing empty auto-VAD turn must NOT blank the per-turn headline. The
// panel selects the display turn via selectDisplayTurn (skips trailing empty-silence) — not raw turns[last].
describe('MetricsPanel — trailing empty auto-VAD turn does not blank the headline (Finding C)', () => {
  const base = Date.parse('2026-06-01T00:00:00.000Z')
  const at = (ms: number) => new Date(base + ms).toISOString()
  const ev = (name: string, ms: number, stage: LatencyStage = 'overall'): LatencyEvent => ({
    name,
    stage,
    timestamp: at(ms),
    relativeMs: ms,
    clockSource: 'browser',
    metadata: {},
  })

  it('keeps showing the GOOD turn metrics after a spurious empty-silence turn lands as turns[last]', () => {
    // GOOD cascade turn: a source transcript + responsiveness markers → headline 800 ms; complete it
    // (moves into turns[], currentTurn cleared).
    sessionStore.beginTurn({
      turnId: 'good',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })
    sessionStore.appendTranscriptSegment({
      segmentId: 'good-s',
      role: 'source',
      text: 'hello',
      isFinal: true,
      provider: 'deepgram',
      timestamp: '2026-06-01T00:00:00.000Z',
      clockSource: 'server',
    })
    sessionStore.appendLatencyEvent(ev('turn.recording.started', 0))
    sessionStore.appendLatencyEvent(ev('stt.final', 1000, 'stt'))
    sessionStore.appendLatencyEvent(ev('tts.first_audio', 1800, 'tts')) // responsiveness = 800 ms
    sessionStore.appendLatencyEvent(ev('turn.recording.stopped', 4000))
    sessionStore.completeTurn('good', 'completed')

    // EMPTY auto-VAD turn: no transcript, no cost, no markers → completes as the NEW turns[last].
    sessionStore.beginTurn({
      turnId: 'empty',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })
    sessionStore.completeTurn('empty', 'completed')

    render(<MetricsPanel />)

    // RED today (turns[last] = empty → n/a); GREEN with selectDisplayTurn skipping the empty turn.
    const headline = screen.getByLabelText('turn-headline')
    expect(headline).toHaveTextContent('800 ms')
  })
})
