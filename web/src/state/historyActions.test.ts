import { describe, expect, it, vi } from 'vitest'
import { loadHistory, loadSessionDetail } from './historyActions'
import { ApiError } from '../api/http'
import type { InterpretationSession, SessionListItem } from '../types/domain'

// DI'd history fetch (web §7): unit-tested against a mocked api + a store spy. loadHistory RETURNS the
// list for the panel's transient display state (a read-only browse — NOT UiSessionState, like the eval
// panel's result §20); a failure routes a sanitized UiError to the store sink (§2) + returns null.

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
    label: null,
    startedAt: '2026-05-30T09:00:00+00:00',
    endedAt: null,
    turnCount: 1,
    modes: ['cascade'],
  },
]

function setup() {
  const store = { addError: vi.fn() }
  const api = { listSessions: vi.fn(), getSession: vi.fn() }
  return { store, api }
}

const sessionDetail = { sessionId: 'session_2', turns: [] } as unknown as InterpretationSession

describe('loadHistory', () => {
  it('returns the list verbatim on success (no re-sort — the backend orders most-recent-first) + no error', async () => {
    const { store, api } = setup()
    api.listSessions.mockResolvedValue(items)

    const result = await loadHistory({ store, api })

    expect(result).toEqual(items)
    expect(result?.[0].sessionId).toBe('session_2') // order preserved
    expect(store.addError).not.toHaveBeenCalled()
  })

  it('routes the backend sessions.read_failed UiError to the store and returns null (ApiError arm)', async () => {
    const { store, api } = setup()
    api.listSessions.mockRejectedValue(
      new ApiError({
        code: 'sessions.read_failed',
        safeMessage: 'Could not read sessions.',
        retryable: true,
      }),
    )

    const result = await loadHistory({ store, api })

    expect(result).toBeNull()
    expect(store.addError).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'sessions.read_failed' }),
    )
  })

  it('degrades a non-ApiError to a fixed sessions.read_failed (no raw leak) and returns null', async () => {
    const { store, api } = setup()
    api.listSessions.mockRejectedValue(new Error('raw-network-detail'))

    const result = await loadHistory({ store, api })

    expect(result).toBeNull()
    const added = store.addError.mock.calls[0][0]
    expect(added.code).toBe('sessions.read_failed')
    expect(added.safeMessage).not.toContain('raw-network-detail') // fixed generic, never the raw error
  })
})

// loadSessionDetail (071 drill-in): fetch ONE session's full detail (GET /api/sessions/{id} — the 068
// disk-fallback). Mirrors loadHistory: RETURNS the session for the accordion's transient cache; a failure
// routes a sanitized UiError to the store sink (§2) + returns null (the row shows no detail, no crash).
describe('loadSessionDetail', () => {
  it('returns the full session on success + no error', async () => {
    const { store, api } = setup()
    api.getSession.mockResolvedValue(sessionDetail)

    const result = await loadSessionDetail({ store, api }, 'session_2')

    expect(api.getSession).toHaveBeenCalledWith('session_2')
    expect(result).toBe(sessionDetail)
    expect(store.addError).not.toHaveBeenCalled()
  })

  it('routes the backend sanitized UiError to the store and returns null (ApiError arm)', async () => {
    const { store, api } = setup()
    api.getSession.mockRejectedValue(
      new ApiError({
        code: 'sessions.read_failed',
        safeMessage: 'Could not read the session.',
        retryable: true,
      }),
    )

    const result = await loadSessionDetail({ store, api }, 'session_2')

    expect(result).toBeNull()
    expect(store.addError).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'sessions.read_failed' }),
    )
  })

  it('degrades a non-ApiError to a fixed sessions.read_failed (no raw leak) and returns null', async () => {
    const { store, api } = setup()
    api.getSession.mockRejectedValue(new Error('raw-detail-leak'))

    const result = await loadSessionDetail({ store, api }, 'session_2')

    expect(result).toBeNull()
    const added = store.addError.mock.calls[0][0]
    expect(added.code).toBe('sessions.read_failed')
    expect(added.safeMessage).not.toContain('raw-detail-leak')
  })
})
