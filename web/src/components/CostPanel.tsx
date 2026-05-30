import { formatCostPerMinute, formatUsdPerMinute } from '../state/selectors'
import { useSessionState } from '../state/sessionStore'
import type { TurnViewModel } from '../types/domain'

// Cost panel (ARCH-007 / ARCH-014). Always-qualified "Estimated $X.XX/min" (never a billed figure), the
// model used, and the estimate's assumptions in a tooltip/disclosure. Per-turn from currentTurn.cost
// (live); per-mode from the backend summary. Unavailable -> 'n/a'. Manual-smoke; formatting is unit-
// tested in selectors.test.ts.

export default function CostPanel() {
  const state = useSessionState()
  const turn: TurnViewModel | undefined = state.currentTurn ?? state.turns[state.turns.length - 1]
  const cost = turn?.cost
  const assumptions = cost?.assumptions ?? []
  const cascadePerMin = state.summary?.cascade?.estimatedCostPerMinuteUsd
  const realtimePerMin = state.summary?.realtime?.estimatedCostPerMinuteUsd

  return (
    <section aria-label="cost">
      <h2>Cost</h2>

      <div aria-label="turn-cost">
        <h3>Current turn</h3>
        <p title={assumptions.join(' · ') || undefined}>
          {formatCostPerMinute(cost)}
          {cost?.model ? ` · model: ${cost.model}` : ''}
        </p>
        {assumptions.length > 0 && (
          <details>
            <summary>Estimate assumptions</summary>
            <ul>
              {assumptions.map((a, i) => (
                <li key={`assumption-${i}`}>{a}</li>
              ))}
            </ul>
          </details>
        )}
      </div>

      <div aria-label="session-cost">
        <h3>Session (per mode)</h3>
        <ul>
          <li>Cascade: {formatUsdPerMinute(cascadePerMin)}</li>
          <li>Realtime: {formatUsdPerMinute(realtimePerMin)}</li>
        </ul>
      </div>

      <p>
        <small>
          All cost figures are estimates from configured PAYG rates — not billed amounts.
        </small>
      </p>
    </section>
  )
}
