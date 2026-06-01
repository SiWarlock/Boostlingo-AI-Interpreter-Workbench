// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import TurnCard from './TurnCard'
import type { TurnViewModel } from '../types/domain'

// Phase J / J.4 — one turn's card: a direction badge + the turn's source/target text (decision c · Option
// A). Preserves the partial-streaming render (data-final) + the realtime "source unavailable" note (PRD
// must-have 6). web §14 (per-file jsdom + cleanup); pure presentation over a TurnViewModel prop.
afterEach(cleanup)

function turn(overrides: Partial<TurnViewModel> = {}): TurnViewModel {
  return {
    turnId: 't1',
    mode: 'cascade',
    direction: { source: 'en', target: 'es' },
    status: 'completed',
    startedAt: 't',
    sourceTranscript: [],
    targetTranscript: [],
    latency: {},
    errors: [],
    ...overrides,
  }
}

describe('TurnCard', () => {
  it('shows the direction badge derived from the turn direction', () => {
    render(<TurnCard turn={turn({ direction: { source: 'es', target: 'en' } })} />)
    expect(screen.getByLabelText('direction-badge')).toHaveTextContent('ES → EN')
  })

  it('shows both the source and target text in the card (original/translation pair preserved)', () => {
    render(
      <TurnCard
        turn={turn({
          sourceTranscript: [{ text: 'hello', isFinal: true }],
          targetTranscript: [{ text: 'hola', isFinal: true }],
        })}
      />,
    )
    expect(screen.getByLabelText('source-transcript')).toHaveTextContent('hello')
    expect(screen.getByLabelText('target-transcript')).toHaveTextContent('hola')
  })

  it('renders a non-final segment as a streaming partial (data-final=false) — no streaming regression', () => {
    render(<TurnCard turn={turn({ sourceTranscript: [{ text: 'partial…', isFinal: false }] })} />)
    const [line] = within(screen.getByLabelText('source-transcript')).getAllByRole('listitem')
    expect(line).toHaveAttribute('data-final', 'false')
    expect(line).toHaveTextContent('partial…')
  })

  it('shows an explicit source-unavailable note for a realtime turn with no source (PRD must-have 6)', () => {
    render(
      <TurnCard
        turn={turn({
          mode: 'realtime',
          sourceTranscript: [],
          targetTranscript: [{ text: 'hola', isFinal: false }],
        })}
      />,
    )
    expect(screen.getByLabelText('source-unavailable')).toBeInTheDocument()
    expect(screen.getByLabelText('target-transcript')).toHaveTextContent('hola')
  })
})
