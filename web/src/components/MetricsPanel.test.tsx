// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import MetricsPanel from './MetricsPanel'
import { sessionStore } from '../state/sessionStore'

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
