import { useCallback, useEffect } from 'react'
import { ApiError } from './api/http'
import { configApi } from './api/configApi'
import { sessionsApi } from './api/sessionsApi'
import ComparisonSummary from './components/ComparisonSummary'
import CostPanel from './components/CostPanel'
import ErrorBanner from './components/ErrorBanner'
import EvaluationPanel from './components/EvaluationPanel'
import MetricsPanel from './components/MetricsPanel'
import ModeToggle from './components/ModeToggle'
import RecordingControls from './components/RecordingControls'
import SessionSetup from './components/SessionSetup'
import StatusPill from './components/StatusPill'
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
  // Provider chips: presence-only health (invariant #1) — configured -> green dot, else muted gray.
  const providerChips: { key: string; label: string; configured: boolean }[] = health
    ? [
        { key: 'realtime', label: 'Realtime', configured: health.realtime.configured },
        { key: 'stt', label: 'STT', configured: health.cascade.stt.configured },
        {
          key: 'translation',
          label: 'Translation',
          configured: health.cascade.translation.configured,
        },
        { key: 'tts', label: 'TTS', configured: health.cascade.tts.configured },
      ]
    : []

  return (
    <main className="wb-shell">
      <header>
        <div className="header-row">
          <img className="header-mark" src="/mark.svg" alt="" />
          <div style={{ flex: 1 }}>
            <h1 className="header-title">AI Interpreter Workbench</h1>
            <div className="header-sub">
              Realtime vs Cascade · live latency, cost &amp; quality · EN ⇄ ES
            </div>
          </div>
          <StatusPill value={state.sessionStatus} large />
        </div>
        <section aria-label="provider-health" className="chips-row">
          <span className="chips-lab">Providers</span>
          {health ? (
            providerChips.map((p) => (
              <span key={p.key} className={`chip${p.configured ? '' : ' muted'}`}>
                <span
                  className="d"
                  style={{ background: p.configured ? 'var(--success)' : 'var(--metric-na)' }}
                />
                {p.label}
              </span>
            ))
          ) : (
            <span className="chip muted">
              <span className="d" style={{ background: 'var(--metric-na)' }} />
              Loading configuration…
            </span>
          )}
        </section>
      </header>

      <ErrorBanner />

      <div className="wb-grid">
        <div className="wb-stack">
          <ModeToggle />
          <SessionSetup />
          <RecordingControls />
        </div>
        <div className="wb-stack">
          <TranscriptPanel />
        </div>
        <div className="wb-stack">
          <MetricsPanel onRefresh={refreshSummary} />
          <CostPanel />
        </div>
      </div>

      <div className="wb-band">
        <ComparisonSummary />
      </div>
      <div className="wb-band">
        <EvaluationPanel />
      </div>
    </main>
  )
}
