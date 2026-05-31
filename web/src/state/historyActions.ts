import { ApiError } from '../api/http'
import type { SessionStore } from './sessionStore'
import type { SessionListItem } from '../types/domain'

// The session-history fetch (H.3-frontend; ARCH-009 / ARCH-017). DI'd + unit-tested against a mocked api +
// the store (lesson §7); the SessionHistory panel is a thin render+dispatch caller. RETURNS the list for
// the panel's TRANSIENT display state (a read-only browse — NOT UiSessionState, like the eval panel's
// result §20); a fetch failure routes a sanitized UiError to the store sink (the single error surface, §2)
// + returns null. The backend orders most-recent-first → the caller does NOT re-sort (§35).

// The minimal api surface the flow needs (structurally satisfied by sessionsApi).
export type HistoryApi = {
  listSessions(): Promise<SessionListItem[]>
}

export type HistoryDeps = {
  store: Pick<SessionStore, 'addError'>
  api: HistoryApi
}

export async function loadHistory(deps: HistoryDeps): Promise<SessionListItem[] | null> {
  try {
    // Return the list verbatim — the backend already orders most-recent-first (no client re-sort, §35).
    return await deps.api.listSessions()
  } catch (error) {
    // An ApiError carries the already-sanitized backend UiError (the §35 sessions.read_failed 500, or a
    // network/response boundary error); anything else degrades to a fixed sessions.read_failed (no raw
    // leak). Route it to the store sink → the global ErrorBanner renders errorCopy('sessions.read_failed').
    deps.store.addError(
      error instanceof ApiError
        ? error.uiError
        : {
            code: 'sessions.read_failed',
            safeMessage: 'Could not load the session history.',
            retryable: true,
          },
    )
    return null
  }
}
