// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import DirectionBadge from './DirectionBadge'

// Phase J / J.4 — the per-turn direction badge ("EN → ES" / "ES → EN"). Pure presentation over
// turn.direction (decision c · Option A: arrow + uppercase codes). web §14 (per-file jsdom + cleanup).
afterEach(cleanup)

describe('DirectionBadge', () => {
  it('renders the direction as uppercase codes with an arrow (EN → ES / ES → EN)', () => {
    const { rerender } = render(<DirectionBadge direction={{ source: 'en', target: 'es' }} />)
    expect(screen.getByLabelText('direction-badge')).toHaveTextContent('EN → ES')

    rerender(<DirectionBadge direction={{ source: 'es', target: 'en' }} />)
    expect(screen.getByLabelText('direction-badge')).toHaveTextContent('ES → EN')
  })
})
