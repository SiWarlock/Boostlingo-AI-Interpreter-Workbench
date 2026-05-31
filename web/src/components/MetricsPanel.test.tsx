// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import MetricsPanel from './MetricsPanel'
import { sessionStore } from '../state/sessionStore'
import type { LatencyEvent, LatencyStage } from '../types/domain'

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
