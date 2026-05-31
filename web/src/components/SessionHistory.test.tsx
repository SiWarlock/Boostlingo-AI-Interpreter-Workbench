// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Mock the DI'd history fetch — the panel is a thin render+dispatch shell over it (ARCH-007). loadHistory
// is mocked so no transport runs; the component drives its transient local list state from the result.
vi.mock('../state/historyActions', () => ({
  loadHistory: vi.fn(),
}))

import SessionHistory from './SessionHistory'
import { loadHistory } from '../state/historyActions'
import { sessionStore } from '../state/sessionStore'
import type { SessionListItem } from '../types/domain'

const items: SessionListItem[] = [
  {
    sessionId: 'session_2',
    label: 'Recent run',
    startedAt: '2026-05-31T10:00:00+00:00',
    endedAt: '2026-05-31T10:05:00+00:00',
    turnCount: 3,
    modes: ['realtime', 'cascade'],
  },
  {
    sessionId: 'session_1',
    label: null, // no label → the row falls back to the sessionId
    startedAt: '2026-05-30T09:00:00+00:00',
    endedAt: null, // never ended (in-progress / abandoned)
    turnCount: 1,
    modes: ['cascade'],
  },
]

afterEach(() => {
  cleanup()
  sessionStore.reset()
  vi.clearAllMocks()
})

describe('SessionHistory', () => {
  it('fetches on mount and renders each session (label-or-id, turn count, mode chips) most-recent-first', async () => {
    vi.mocked(loadHistory).mockResolvedValue(items)

    render(<SessionHistory />)

    // mount fetch fired
    expect(loadHistory).toHaveBeenCalledTimes(1)

    // the labelled row renders its label; the label-less row falls back to its sessionId
    expect(await screen.findByText('Recent run')).toBeInTheDocument()
    expect(screen.getByText('session_1')).toBeInTheDocument()

    // turn counts render
    expect(screen.getByText(/3 turns/i)).toBeInTheDocument()
    expect(screen.getByText(/1 turn/i)).toBeInTheDocument()

    // order preserved verbatim (the backend orders most-recent-first; the view does NOT re-sort)
    const rows = screen.getAllByRole('listitem')
    expect(within(rows[0]).getByText('Recent run')).toBeInTheDocument()
    expect(within(rows[1]).getByText('session_1')).toBeInTheDocument()

    // a two-mode session renders BOTH chips (the workbench's whole premise is the 2-mode comparison)
    expect(within(rows[0]).getByText(/realtime/i)).toBeInTheDocument()
    expect(within(rows[0]).getByText(/cascade/i)).toBeInTheDocument()

    // the started time renders (format-tolerant — assert the year, which is timezone-stable, not the exact format)
    expect(within(rows[0]).getAllByText(/2026/).length).toBeGreaterThan(0)

    // the never-ended (endedAt:null) row renders the "in progress" branch (not a blank / a crash)
    expect(within(rows[1]).getByText(/in progress/i)).toBeInTheDocument()
  })

  it('renders an explicit "no past sessions" note when the list is empty', async () => {
    vi.mocked(loadHistory).mockResolvedValue([])

    render(<SessionHistory />)

    expect(await screen.findByText(/no past sessions/i)).toBeInTheDocument()
  })

  it('the Refresh control re-dispatches loadHistory', async () => {
    vi.mocked(loadHistory).mockResolvedValue([])
    render(<SessionHistory />)
    await screen.findByText(/no past sessions/i) // mount fetch settled (call #1)

    fireEvent.click(screen.getByRole('button', { name: /refresh/i }))

    expect(loadHistory).toHaveBeenCalledTimes(2) // mount + the manual refresh
  })

  it('a failed refresh (loadHistory returns null) does NOT wipe the existing list — the error routes to the banner', async () => {
    vi.mocked(loadHistory).mockResolvedValueOnce(items) // mount populates the list
    render(<SessionHistory />)
    await screen.findByText('Recent run') // list rendered

    vi.mocked(loadHistory).mockResolvedValueOnce(null) // the refresh fails (error already on the store sink)
    fireEvent.click(screen.getByRole('button', { name: /refresh/i }))

    // the `if (list)` guard holds → the prior rows stay (null is NOT applied as an empty list)
    expect(await screen.findByText('Recent run')).toBeInTheDocument()
    expect(screen.getByText('session_1')).toBeInTheDocument()
    expect(loadHistory).toHaveBeenCalledTimes(2)
  })
})
