// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import TranscriptPanel from './TranscriptPanel'
import { sessionStore } from '../state/sessionStore'
import type { TranscriptSegment } from '../types/domain'

// Characterization tests of the "source unavailable" behavior already present in TranscriptPanel (added
// defensively in D.6/D.7 for realtime reuse). E.4b is the first slice that actually exercises the realtime
// branch, so these pin PRD must-have 6 (never silently hide the source row when realtime input transcription
// is off). web §14 (per-file jsdom + cleanup).

function seg(role: 'source' | 'target', text: string, isFinal: boolean): TranscriptSegment {
  return { segmentId: `${role}-1`, role, text, isFinal, provider: 'openai-realtime', timestamp: 't', clockSource: 'browser' }
}

afterEach(() => {
  cleanup()
  sessionStore.reset()
})

describe('TranscriptPanel — source unavailable (PRD must-have 6)', () => {
  it('shows an explicit "source unavailable" note for a realtime turn with no source segments', () => {
    sessionStore.beginTurn({ turnId: 't1', mode: 'realtime', direction: { source: 'en', target: 'es' } })
    sessionStore.appendTranscriptSegment(seg('target', 'hola', false))

    render(<TranscriptPanel />)

    expect(screen.getByLabelText('source-unavailable')).toBeInTheDocument()
    expect(screen.getByLabelText('target-transcript')).toHaveTextContent('hola')
  })

  it('renders both source and target transcripts when both are present', () => {
    sessionStore.beginTurn({ turnId: 't2', mode: 'realtime', direction: { source: 'en', target: 'es' } })
    sessionStore.appendTranscriptSegment(seg('source', 'hello', true))
    sessionStore.appendTranscriptSegment(seg('target', 'hola', false))

    render(<TranscriptPanel />)

    expect(screen.queryByLabelText('source-unavailable')).not.toBeInTheDocument()
    expect(screen.getByLabelText('source-transcript')).toHaveTextContent('hello')
    expect(screen.getByLabelText('target-transcript')).toHaveTextContent('hola')
  })
})
