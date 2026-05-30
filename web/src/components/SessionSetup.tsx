import { sessionsApi } from '../api/sessionsApi'
import { realtimeConnectionManager } from '../realtime/realtimeConnectionManager'
import { endSession, startSession } from '../state/sessionActions'
import { availableModels } from '../state/selectors'
import { sessionStore, useSessionState } from '../state/sessionStore'
import type { LanguageDirection, RealtimeModel, TranslationModel } from '../types/domain'

// Session setup form (ARCH-007). Renders from the store; every edit writes through
// updateSessionConfig (store = live config source-of-truth); Start/End delegate to the testable
// sessionActions orchestration. Clean separation: no transport internals — just store reads, intent
// dispatches, and the API client passed into the orchestration deps.
const DIRECTIONS: { label: string; value: LanguageDirection }[] = [
  { label: 'English → Spanish', value: { source: 'en', target: 'es' } },
  { label: 'Spanish → English', value: { source: 'es', target: 'en' } },
]

function directionKey(direction: LanguageDirection): string {
  return `${direction.source}-${direction.target}`
}

export default function SessionSetup() {
  const state = useSessionState()
  const models = availableModels(state.providerHealth)
  const starting = state.sessionStatus === 'starting'
  const active = state.sessionStatus === 'active' || state.sessionStatus === 'readyForTurn'

  return (
    <section aria-label="session-setup">
      <label>
        Label
        <input
          type="text"
          value={state.label ?? ''}
          onChange={(e) => sessionStore.updateSessionConfig({ label: e.target.value })}
        />
      </label>

      <label>
        Direction
        <select
          value={directionKey(state.direction)}
          onChange={(e) => {
            const next = DIRECTIONS.find((d) => directionKey(d.value) === e.target.value)
            if (next) sessionStore.updateSessionConfig({ direction: next.value })
          }}
        >
          {DIRECTIONS.map((d) => (
            <option key={directionKey(d.value)} value={directionKey(d.value)}>
              {d.label}
            </option>
          ))}
        </select>
      </label>

      <label>
        Realtime model
        <select
          value={state.realtimeModel}
          onChange={(e) =>
            sessionStore.updateSessionConfig({ realtimeModel: e.target.value as RealtimeModel })
          }
        >
          {models.realtimeModels.map((model) => (
            <option key={model} value={model}>
              {model}
            </option>
          ))}
        </select>
      </label>

      <label>
        Translation model
        <select
          value={state.translationModel}
          onChange={(e) =>
            sessionStore.updateSessionConfig({
              translationModel: e.target.value as TranslationModel,
            })
          }
        >
          {models.translationModels.map((model) => (
            <option key={model} value={model}>
              {model}
            </option>
          ))}
        </select>
      </label>

      <button
        type="button"
        disabled={starting || active}
        onClick={() => void startSession({ store: sessionStore, api: sessionsApi })}
      >
        {starting ? 'Starting…' : 'Start session'}
      </button>
      <button
        type="button"
        disabled={!active}
        onClick={() => {
          void endSession({ store: sessionStore, api: sessionsApi })
          // Tear down the realtime connection on End (idempotent — a no-op for cascade). E.5a.
          realtimeConnectionManager.teardown()
        }}
      >
        End session
      </button>
    </section>
  )
}
