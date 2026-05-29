import { canToggleMode, modeAvailability } from '../state/selectors'
import { sessionStore, useSessionState } from '../state/sessionStore'
import type { InterpretationMode } from '../types/domain'

// Mode selector (ARCH-007). Renders from the store's `mode`; a mode is disabled when its providers
// are unconfigured (modeAvailability) OR a turn is in flight (canToggleMode). Selecting an enabled
// mode writes via updateSessionConfig. Clean separation: renders from the store, dispatches an
// intent — no transport internals. (Full Flow-G realtime teardown/re-mint is Phase E.)
const MODES: { value: InterpretationMode; label: string }[] = [
  { value: 'cascade', label: 'Cascade' },
  { value: 'realtime', label: 'Realtime' },
]

export default function ModeToggle() {
  const state = useSessionState()
  const availability = modeAvailability(state.providerHealth)
  const toggleAllowed = canToggleMode(state.turnStatus)

  return (
    <fieldset aria-label="mode-toggle">
      <legend>Mode</legend>
      {MODES.map(({ value, label }) => (
        <button
          key={value}
          type="button"
          aria-pressed={state.mode === value}
          disabled={!availability[value] || !toggleAllowed}
          onClick={() => sessionStore.updateSessionConfig({ mode: value })}
        >
          {label}
        </button>
      ))}
    </fieldset>
  )
}
