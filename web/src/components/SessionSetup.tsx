import { Headphones, Play, Power } from 'lucide-react'
import { sessionsApi } from '../api/sessionsApi'
import { realtimeConnectionManager } from '../realtime/realtimeConnectionManager'
import { endSession, startSession } from '../state/sessionActions'
import { availableModels, canToggleMode } from '../state/selectors'
import { sessionStore, useSessionState } from '../state/sessionStore'
import type { LanguageDirection, RealtimeModel, TranslationModel } from '../types/domain'

// Session setup form (ARCH-007). Renders from the store; every edit writes through
// updateSessionConfig (store = live config source-of-truth); Start/End delegate to the testable
// sessionActions orchestration. Clean separation: no transport internals — just store reads, intent
// dispatches, and the API client passed into the orchestration deps.
//
// H.1 styling: the design's card + .field/.select form, styled Start/End buttons. CSS/markup only —
// both buttons stay always-rendered with the SAME disabled gating, Direction stays a <select> (the
// kit's dir-swap click is a different interaction = logic, out of scope). Labels keep their implicit
// control association.
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
    <section className="card card-pad" aria-label="session-setup">
      <div className="card-hd">
        <span className="ic">
          <Headphones size={18} aria-hidden />
        </span>
        <span className="card-title">Session</span>
      </div>

      <label className="field">
        <span className="field-lab">Label</span>
        <input
          className="input"
          type="text"
          placeholder="e.g. clinic intake demo"
          value={state.label ?? ''}
          onChange={(e) => sessionStore.updateSessionConfig({ label: e.target.value })}
        />
      </label>

      <label className="field">
        <span className="field-lab">Direction</span>
        <select
          className="select"
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

      <label className="field">
        <span className="field-lab">Realtime model</span>
        <select
          className="select"
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

      <label className="field">
        <span className="field-lab">Translation model</span>
        <select
          className="select"
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

      {/* Turn control (Phase I) — Manual (click Start/Stop) | Auto-VAD (server segments speech). KEEPS
          Manual (default); gated mid-turn (canToggleMode) like the ModeToggle. Session-level config. */}
      <fieldset className="field" aria-label="turn-control">
        <span className="field-lab">Turn control</span>
        <div className={`seg${canToggleMode(state.turnStatus) ? '' : ' locked'}`}>
          {(['manual', 'auto'] as const).map((m) => (
            <button
              key={m}
              type="button"
              className={`seg-opt${state.turnControlMode === m ? ' active' : ''}`}
              aria-pressed={state.turnControlMode === m}
              disabled={!canToggleMode(state.turnStatus)}
              onClick={() => sessionStore.setTurnControlMode(m)}
            >
              {m === 'manual' ? 'Manual' : 'Auto-VAD'}
            </button>
          ))}
        </div>
      </fieldset>

      {/* Bidirectional / auto-detect (Phase J) — when on, the source language is auto-detected per utterance
          and direction flips to the other language (both modes). Additive; default off (one-direction).
          Pre-session-oriented (value consumed at session/turn start, like Direction). Enabling it defaults
          turn-control to Auto-VAD (hands-free back-and-forth); the flags stay independent (the user can
          switch turn-control back to Manual). The button's accessible name is "Bidirectional" only. */}
      <fieldset className="field" aria-label="bidirectional">
        <span className="field-lab">Auto-detect language</span>
        <div className="seg">
          <button
            type="button"
            className={`seg-opt${state.bidirectional ? ' active' : ''}`}
            aria-pressed={state.bidirectional}
            onClick={() => {
              const next = !state.bidirectional
              sessionStore.updateSessionConfig({ bidirectional: next })
              // Hands-free default on enable (reversible) — but only when turn-control is itself togglable
              // (canToggleMode), so the coupling can't flip the mode mid-turn (the turn-control toggle is
              // gated the same way). The bidirectional flag itself is pre-session-consumed, so it's safe to
              // set anytime (like the Direction selector).
              if (next && canToggleMode(state.turnStatus)) {
                sessionStore.setTurnControlMode('auto')
              }
            }}
          >
            Bidirectional
          </button>
        </div>
      </fieldset>

      <div className="session-actions">
        <button
          type="button"
          className="btn btn-dark"
          disabled={starting || active}
          onClick={() => void startSession({ store: sessionStore, api: sessionsApi })}
        >
          <span className="ic">
            <Play size={16} aria-hidden />
          </span>
          {starting ? 'Starting…' : 'Start session'}
        </button>
        <button
          type="button"
          className="btn btn-danger"
          disabled={!active}
          onClick={() => {
            void endSession({ store: sessionStore, api: sessionsApi })
            // Tear down the realtime connection on End (idempotent — a no-op for cascade). E.5a.
            realtimeConnectionManager.teardown()
          }}
        >
          <span className="ic">
            <Power size={16} aria-hidden />
          </span>
          End session
        </button>
      </div>
    </section>
  )
}
