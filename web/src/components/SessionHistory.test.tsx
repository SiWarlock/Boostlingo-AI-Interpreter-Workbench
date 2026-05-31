// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Mock the DI'd history fetches — the panel is a thin render+dispatch shell over them (ARCH-007); no
// transport runs. SessionDetail is mocked to a marker (it has its own test) so these tests focus on the
// accordion mechanics (expand / fetch-once-cache / single-open), not the detail rendering.
vi.mock('../state/historyActions', () => ({
  loadHistory: vi.fn(),
  loadSessionDetail: vi.fn(),
}))
vi.mock('./SessionDetail', () => ({
  default: ({ session }: { session: { sessionId: string } }) => (
    <div data-testid="session-detail">detail:{session.sessionId}</div>
  ),
}))

import SessionHistory from './SessionHistory'
import { loadHistory, loadSessionDetail } from '../state/historyActions'
import { sessionStore } from '../state/sessionStore'
import type { InterpretationSession, SessionListItem } from '../types/domain'

const detailFor = (id: string) => ({ sessionId: id, turns: [] }) as unknown as InterpretationSession

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

  // --- 071 drill-in: bounded scroll + click-to-expand accordion ---------------------------------
  it('wraps the list in a bounded-scroll container (the cap is applied)', async () => {
    vi.mocked(loadHistory).mockResolvedValue(items)
    render(<SessionHistory />)
    await screen.findByText('Recent run')

    expect(screen.getByTestId('history-scroll')).toHaveClass('hist-scroll') // fixed-height + overflow-y:auto
  })

  it('clicking a row expands it inline + fetches the detail ONCE (re-expand uses the cache, Q2)', async () => {
    vi.mocked(loadHistory).mockResolvedValue(items)
    vi.mocked(loadSessionDetail).mockResolvedValue(detailFor('session_2'))
    render(<SessionHistory />)
    await screen.findByText('Recent run')

    fireEvent.click(screen.getByRole('button', { name: /Recent run/i }))
    expect(await screen.findByTestId('session-detail')).toHaveTextContent('detail:session_2')
    expect(loadSessionDetail).toHaveBeenCalledTimes(1)
    expect(loadSessionDetail).toHaveBeenCalledWith(expect.anything(), 'session_2')

    // collapse
    fireEvent.click(screen.getByRole('button', { name: /Recent run/i }))
    expect(screen.queryByTestId('session-detail')).not.toBeInTheDocument()

    // re-expand → served from cache, NO refetch (a past session is immutable)
    fireEvent.click(screen.getByRole('button', { name: /Recent run/i }))
    expect(await screen.findByTestId('session-detail')).toBeInTheDocument()
    expect(loadSessionDetail).toHaveBeenCalledTimes(1) // still ONE — fetch-once-cache
  })

  it('single-open: expanding another row collapses the first', async () => {
    vi.mocked(loadHistory).mockResolvedValue(items)
    vi.mocked(loadSessionDetail).mockImplementation((_deps, id: string) =>
      Promise.resolve(detailFor(id)),
    )
    render(<SessionHistory />)
    await screen.findByText('Recent run')

    fireEvent.click(screen.getByRole('button', { name: /Recent run/i })) // session_2
    expect(await screen.findByTestId('session-detail')).toHaveTextContent('detail:session_2')

    fireEvent.click(screen.getByRole('button', { name: /session_1/i })) // session_1
    expect(await screen.findByTestId('session-detail')).toHaveTextContent('detail:session_1')
    expect(screen.getAllByTestId('session-detail')).toHaveLength(1) // single-open — only ONE detail shown
  })

  it('a detail-fetch failure (loadSessionDetail null) shows an inline note + no crash (error via the banner)', async () => {
    vi.mocked(loadHistory).mockResolvedValue(items)
    vi.mocked(loadSessionDetail).mockResolvedValue(null) // the action already routed a sanitized error to the sink
    render(<SessionHistory />)
    await screen.findByText('Recent run')

    fireEvent.click(screen.getByRole('button', { name: /Recent run/i }))

    expect(await screen.findByText(/details unavailable/i)).toBeInTheDocument() // inline, not a crash
    expect(screen.queryByTestId('session-detail')).not.toBeInTheDocument()
    expect(loadSessionDetail).toHaveBeenCalledTimes(1)
  })
})
