import { useCallback, useEffect } from 'react'
import { ApiError } from './api/http'
import { configApi } from './api/configApi'
import { sessionsApi } from './api/sessionsApi'
import CostPanel from './components/CostPanel'
import MetricsPanel from './components/MetricsPanel'
import ModeToggle from './components/ModeToggle'
import RecordingControls from './components/RecordingControls'
import SessionSetup from './components/SessionSetup'
import TranscriptPanel from './components/TranscriptPanel'
import { sessionStore, useSessionState } from './state/sessionStore'

// The mode-agnostic app shell. Renders ONLY from sessionStore state (clean separation, ARCH-007 /
// forbidden-pattern #3 — no transport-client internals here). On mount it runs the Flow-A config
// bootstrap (GET /api/config -> store.loadConfig); a failure dispatches store.addError so there is
// never an unhandled rejection. Panels/controls land in later D slices.
export default function App() {
  const state = useSessionState()

  useEffect(() => {
    let cancelled = false
    configApi
      .getConfig()
      .then((config) => {
        if (!cancelled) sessionStore.loadConfig(config)
      })
      .catch((error: unknown) => {
        if (cancelled) return
        const uiError =
          error instanceof ApiError
            ? error.uiError
            : {
                code: 'config.load_failed',
                safeMessage: 'Could not load server configuration.',
                retryable: true,
              }
        sessionStore.addError(uiError)
      })
    return () => {
      cancelled = true
    }
  }, [])

  // Session-averages source (ARCH-009 / ARCH-014): fetch the backend-canonical summary -> store. The
  // shell owns the fetch (mirrors the config bootstrap above); the panels stay dumb store projections.
  // Sanitized failures go to the error sink — never an unhandled rejection.
  const refreshSummary = useCallback(() => {
    const sessionId = sessionStore.getState().sessionId
    if (!sessionId) return
    sessionsApi
      .getSummary(sessionId)
      .then((summary) => sessionStore.setSummary(summary))
      .catch((error: unknown) => {
        const uiError =
          error instanceof ApiError
            ? error.uiError
            : {
                code: 'summary.load_failed',
                safeMessage: 'Could not load the session summary.',
                retryable: true,
              }
        sessionStore.addError(uiError)
      })
  }, [])

  // Refresh the summary each time a turn finalizes (turns grows) — plus the manual Refresh button.
  useEffect(() => {
    if (state.sessionId && state.turns.length > 0) {
      refreshSummary()
    }
  }, [state.sessionId, state.turns.length, refreshSummary])

  const health = state.providerHealth

  return (
    <main>
      <h1>AI Interpreter Workbench</h1>
      <p>
        Session status: <strong>{state.sessionStatus}</strong>
      </p>
      <section aria-label="provider-health">
        {health ? (
          <ul>
            <li>Realtime: {health.realtime.configured ? 'configured' : 'unavailable'}</li>
            <li>Cascade STT: {health.cascade.stt.configured ? 'configured' : 'unavailable'}</li>
            <li>
              Cascade Translation:{' '}
              {health.cascade.translation.configured ? 'configured' : 'unavailable'}
            </li>
            <li>Cascade TTS: {health.cascade.tts.configured ? 'configured' : 'unavailable'}</li>
          </ul>
        ) : (
          <p>Loading configuration…</p>
        )}
      </section>
      <ModeToggle />
      <SessionSetup />
      <RecordingControls />
      <TranscriptPanel />
      <MetricsPanel onRefresh={refreshSummary} />
      <CostPanel />
      {state.errors.length > 0 && (
        <section aria-label="errors">
          <ul>
            {state.errors.map((error, index) => (
              <li key={`${error.code}-${index}`}>{error.safeMessage}</li>
            ))}
          </ul>
        </section>
      )}
    </main>
  )
}
