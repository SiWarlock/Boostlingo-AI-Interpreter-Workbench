// @vitest-environment jsdom
import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import CostPanel from './CostPanel'
import { sessionStore } from '../state/sessionStore'
import type { CostEstimate } from '../types/domain'

// Finding C (cost sibling): after a GOOD priced turn, a trailing empty auto-VAD turn must NOT blank the
// per-turn cost. The panel selects its display turn via selectDisplayTurn (skips trailing empty-silence),
// not raw turns[last]. web §14 (per-file jsdom + cleanup).

afterEach(() => {
  cleanup()
  sessionStore.reset()
})

function costEstimate(): CostEstimate {
  return {
    provider: 'cascade',
    model: 'gpt-5-nano',
    pricingBasis: 'composite',
    estimatedUsd: 0.0021,
    estimatedUsdPerMinute: 0.42,
    units: {},
    pricingConfigVersion: '2026-05-28-payg-estimates',
    assumptions: [],
  }
}

describe('CostPanel — trailing empty auto-VAD turn does not blank the per-turn cost (Finding C)', () => {
  it('keeps showing the GOOD turn cost after a spurious empty-silence turn lands as turns[last]', () => {
    // GOOD cascade turn: a transcript + a cost estimate → complete it (moves into turns[]).
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
    sessionStore.setTurnCost(costEstimate())
    sessionStore.completeTurn('good', 'completed')

    // EMPTY auto-VAD turn: no transcript, no cost → completes as the NEW turns[last].
    sessionStore.beginTurn({
      turnId: 'empty',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })
    sessionStore.completeTurn('empty', 'completed')

    render(<CostPanel />)

    // RED today (turns[last] = empty → cost n/a); GREEN with selectDisplayTurn skipping the empty turn.
    const turnCost = screen.getByLabelText('turn-cost')
    expect(within(turnCost).getByText('Estimated $0.42/min')).toBeInTheDocument()
    expect(within(turnCost).getByText('gpt-5-nano')).toBeInTheDocument()
  })
})
