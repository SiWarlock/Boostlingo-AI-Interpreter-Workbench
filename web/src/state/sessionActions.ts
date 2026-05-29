import { ApiError } from '../api/http'
import type {
  CreateSessionRequest,
  EndSessionResponse,
  InterpretationSession,
} from '../types/domain'
import type { SessionStore } from './sessionStore'

// Start/End orchestration, extracted from the components so the multi-step flow (clearErrors ->
// starting -> create -> started / error+revert; end -> ended + warning / error) is unit-testable
// without a render. Dependency-injected: production wiring passes { store: sessionStore, api:
// sessionsApi }; tests pass a real createSessionStore() + a mocked api.

// The minimal API surface these flows need (structurally satisfied by sessionsApi).
export type SessionApi = {
  createSession(request: CreateSessionRequest): Promise<InterpretationSession>
  endSession(sessionId: string): Promise<EndSessionResponse>
}

export type SessionActionDeps = {
  store: SessionStore
  api: SessionApi
}

// Build the create request from the store's live config (the form wrote it via updateSessionConfig).
function toCreateRequest(store: SessionStore): CreateSessionRequest {
  const s = store.getState()
  return {
    label: s.label,
    mode: s.mode,
    direction: s.direction,
    realtimeModel: s.realtimeModel,
    translationModel: s.translationModel,
  }
}

export async function startSession({ store, api }: SessionActionDeps): Promise<void> {
  // Clear any prior transient error FIRST so it can't linger past a successful start.
  store.clearErrors()
  store.setSessionStatus('starting')
  try {
    const session = await api.createSession(toCreateRequest(store))
    store.sessionStarted(session)
  } catch (error) {
    store.addError(
      error instanceof ApiError
        ? error.uiError
        : {
            code: 'session.start_failed',
            safeMessage: 'Could not start the session.',
            retryable: true,
          },
    )
    // Revert out of the in-flight 'starting' status — the session was not created.
    store.setSessionStatus('configured')
  }
}

export async function endSession({ store, api }: SessionActionDeps): Promise<void> {
  const { sessionId } = store.getState()
  if (sessionId === null) {
    return
  }
  try {
    const response = await api.endSession(sessionId)
    store.sessionEnded()
    // Best-effort persistence (ARCH-016): a write failure comes back as a 200 body warning, surfaced
    // (not thrown) — the session still ended server-side.
    if (response.persistenceWarning) {
      store.addError(response.persistenceWarning)
    }
  } catch (error) {
    // A fetch-rejection / 404 (not the 200-never-500 /end path): surface it; the session did NOT end
    // server-side, so keep status 'active' (do not call sessionEnded()).
    store.addError(
      error instanceof ApiError
        ? error.uiError
        : {
            code: 'session.end_failed',
            safeMessage: 'Could not end the session.',
            retryable: true,
          },
    )
  }
}
