import { Layers, SlidersHorizontal, Zap } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { realtimeConnectionManager } from '../realtime/realtimeConnectionManager'
import { canToggleMode, modeAvailability } from '../state/selectors'
import { sessionStore, useSessionState } from '../state/sessionStore'
import type { InterpretationMode } from '../types/domain'

// Mode selector (ARCH-007). Renders from the store's `mode`; a mode is disabled when its providers
// are unconfigured (modeAvailability) OR a turn is in flight (canToggleMode). Selecting an enabled
// mode writes via updateSessionConfig. Clean separation: renders from the store, dispatches an
// intent — no transport internals. (Full Flow-G realtime teardown/re-mint is Phase E.)
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
              onClick={() => {
                if (value === state.mode) {
                  return // clicking the already-active mode is a no-op (no spurious store write / teardown)
                }
                // Flow G (E.5b): tear down the realtime connection on a switch-AWAY from realtime (no double-mic)
                // before flipping the mode. cascade→realtime reconnects lazily on the next turn.
                realtimeConnectionManager.onModeSwitch(state.mode, value)
                sessionStore.updateSessionConfig({ mode: value })
              }}
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
