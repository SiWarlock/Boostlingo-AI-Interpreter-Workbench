import { Layers, SlidersHorizontal, Zap } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { sessionsApi } from '../api/sessionsApi'
import { realtimeConnectionManager } from '../realtime/realtimeConnectionManager'
import { switchMode } from '../state/sessionActions'
import { canToggleMode, modeAvailability } from '../state/selectors'
import { sessionStore, useSessionState } from '../state/sessionStore'
import type { InterpretationMode } from '../types/domain'

// Mode selector (ARCH-007). Renders from the store's `mode`; a mode is disabled when its providers
// are unconfigured (modeAvailability) OR a turn is in flight (canToggleMode). Selecting an enabled mode
// dispatches the DI'd switchMode flow (Finding 2c): it POSTs /api/sessions/{id}/mode so a turn created
// after the switch is stamped with the new mode, resyncs from the response, and gates the §18 Flow-G
// realtime teardown on POST success. Clean separation: the component dispatches an intent — no transport
// detail (the flow + connectionManager own it). Pre-session, switchMode is a pure store write (no POST).
//
// H.1 styling: the design's segmented control (.seg) with a SOLID mode-color active highlight — the
// headline UX fix (blue=Realtime, violet=Cascade). CSS/markup only: the <button> + aria-pressed +
// disabled + onClick are unchanged. The `.sub` tagline is aria-hidden so each button's accessible name
// stays exactly "Cascade"/"Realtime" (the tests query getByRole('button', { name: 'Cascade' | 'Realtime' })).
type ModeMeta = {
  value: InterpretationMode
  label: string
  color: 'blue' | 'violet'
  icon: LucideIcon
  sub: string
}
const MODES: ModeMeta[] = [
  { value: 'cascade', label: 'Cascade', color: 'violet', icon: Layers, sub: 'STT → Trans → TTS' },
  { value: 'realtime', label: 'Realtime', color: 'blue', icon: Zap, sub: 'single live stream' },
]

export default function ModeToggle() {
  const state = useSessionState()
  const availability = modeAvailability(state.providerHealth)
  const toggleAllowed = canToggleMode(state.turnStatus)

  return (
    <fieldset className="card card-pad mode-fieldset" aria-label="mode-toggle">
      <legend className="vh">Mode</legend>
      <div className="card-hd">
        <span className="ic">
          <SlidersHorizontal size={18} aria-hidden />
        </span>
        <span className="card-title">Mode</span>
        {!toggleAllowed && <span className="eyebrow right">locked</span>}
      </div>
      <div className={`seg${toggleAllowed ? '' : ' locked'}`}>
        {MODES.map(({ value, label, color, icon: Icon, sub }) => {
          const available = availability[value]
          const active = state.mode === value
          return (
            <button
              key={value}
              type="button"
              className={`seg-opt ${color}${active ? ' active' : ''}${available ? '' : ' unavail'}`}
              aria-pressed={active}
              disabled={!available || !toggleAllowed}
              onClick={() =>
                // switchMode self-clears prior errors at the start of a real switch (G.4/054 Fix B —
                // clear-before-retry self-recovery), so the toggle is a thin dispatch (clean separation).
                void switchMode(
                  {
                    store: sessionStore,
                    api: sessionsApi,
                    connectionManager: realtimeConnectionManager,
                  },
                  value,
                )
              }
            >
              <span className="top">
                <span className="ic">
                  <Icon size={16} aria-hidden />
                </span>
                {label}
              </span>
              <span className="sub" aria-hidden="true">
                {available ? sub : 'not configured'}
              </span>
            </button>
          )
        })}
      </div>
    </fieldset>
  )
}
