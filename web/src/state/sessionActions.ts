import { ApiError } from '../api/http'
import type { RealtimeConnectionManager } from '../realtime/realtimeConnectionManager'
import type {
  CreateSessionRequest,
  EndSessionResponse,
  InterpretationMode,
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

// Mode-switch orchestration (Finding 2c — ARCH-009 / ARCH-017 Flow G). A turn is stamped with the
// session's CurrentMode at create, so a mid-session switch MUST propagate to the backend (a POST), not just
// the frontend store, or turns are mislabeled + the by-mode comparison is invalid. DI'd (lesson §7): the
// ModeToggle dispatches this intent; the connectionManager is injected so the §18 Flow-G realtime teardown
// is gated on POST SUCCESS (a component-level teardown couldn't know success/failure → would tear down a
// live pc on a failed POST). Errors → the store (single sink, §2/§7).
export type SwitchModeApi = {
  setMode(sessionId: string, mode: InterpretationMode): Promise<InterpretationSession>
}

export type SwitchModeDeps = {
  store: SessionStore
  api: SwitchModeApi
  connectionManager: Pick<RealtimeConnectionManager, 'onModeSwitch'>
}

export async function switchMode(
  { store, api, connectionManager }: SwitchModeDeps,
  target: InterpretationMode,
): Promise<void> {
  const { sessionId, sessionStatus, mode: prevMode } = store.getState()
  if (target === prevMode) {
    return // no-op (idempotent; matches the backend no-op) — a same-mode click has no side effects
  }
  // Clear any prior transient error FIRST (like startSession, §7) so a real switch attempt self-recovers
  // from a lingering failed-switch banner — robust to ANY callsite, not just ModeToggle's clear-before-dispatch
  // (G.4/054 Fix B). After the no-op guard so a same-mode click stays a pure no-op.
  store.clearErrors()
  // Store-only write (no POST) whenever there is NO live backend session: pre-session (sessionId null —
  // Create sends the initial mode) OR after the session ended/ending (don't POST to a dead session — the
  // toggle just configures the NEXT session's mode; G.4/054 Fix A). The compound `||` early-return also
  // narrows sessionId → string for the POST path below. A LIVE session (active OR readyForTurn) POSTs so a
  // turn created after the switch is stamped with the new mode (the 2c fix) — never gate on `=== 'active'`
  // alone, which would skip a live readyForTurn session and silently re-introduce the divergence.
  if (sessionId === null || sessionStatus === 'ended' || sessionStatus === 'ending') {
    connectionManager.onModeSwitch(prevMode, target)
    store.updateSessionConfig({ mode: target })
    return
  }
  try {
    const session = await api.setMode(sessionId, target)
    // Success: finalize — tear down realtime on a switch-AWAY (§18; no-op cascade→realtime), then resync the
    // mode from the authoritative returned session (Q4). Backend + frontend now agree (the 2c fix).
    connectionManager.onModeSwitch(prevMode, target)
    store.updateSessionConfig({ mode: session.config.currentMode })
  } catch {
    // Failure: KEEP the prior mode (no backend/frontend divergence — Q1) + NO teardown (the realtime pc, if
    // any, stays up). Normalize ANY failure (ApiError or not) to a single sanitized frontend code (Q4/054)
    // so errorCopy maps ONE actionable message and no raw backend/http code (e.g. http.404 pre-050) reaches
    // the banner via the generic fallback.
    store.addError({
      code: 'session.mode_switch_failed',
      safeMessage: 'Could not switch the interpretation mode.',
      retryable: true,
    })
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
