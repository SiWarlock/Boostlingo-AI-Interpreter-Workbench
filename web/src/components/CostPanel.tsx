import { DollarSign, Info } from 'lucide-react'
import { formatCostPerMinute, formatUsdPerMinute, selectDisplayTurn } from '../state/selectors'
import { useSessionState } from '../state/sessionStore'

// Cost panel (ARCH-007 / ARCH-014). Always-qualified "Estimated $X.XX/min" (never a billed figure), the
// model used, and the estimate's assumptions in a tooltip/disclosure. Per-turn from currentTurn.cost
// (live); per-mode from the backend summary. Unavailable -> 'n/a'. Manual-smoke; formatting is unit-
// tested in selectors.test.ts.
//
// H.1 styling: card + a mono "Estimated …/min" headline + .kv rows + assumptions disclosure. CSS/markup
// only — the cost / turn-cost / session-cost aria-labels + the formatter outputs are unchanged.

export default function CostPanel() {
  const state = useSessionState()
  // The last MEANINGFUL turn (skips trailing empty auto-VAD silence turns; Finding C) — the SAME shared
  // selector MetricsPanel uses, so the two panels never diverge on which turn they display.
  const turn = selectDisplayTurn(state)
  const cost = turn?.cost
  const assumptions = cost?.assumptions ?? []
  const cascadePerMin = state.summary?.cascade?.estimatedCostPerMinuteUsd
  const realtimePerMin = state.summary?.realtime?.estimatedCostPerMinuteUsd

  return (
    <section className="card card-pad" aria-label="cost">
      <div className="card-hd">
        <span className="ic">
          <DollarSign size={18} aria-hidden />
        </span>
        <span className="card-title">Cost</span>
      </div>

      <div aria-label="turn-cost">
        <div className="eyebrow">This turn · estimated rate</div>
        <div
          className="bl-metric"
          style={{ fontSize: 22, marginTop: 8 }}
          title={assumptions.join(' · ') || undefined}
        >
          {formatCostPerMinute(cost)}
        </div>
        <div style={{ marginTop: 10 }}>
          <div className="kv">
            <span className="k">Model</span>
            <span className="v">{cost?.model ?? <span className="na">n/a</span>}</span>
          </div>
        </div>
        {assumptions.length > 0 && (
          <details style={{ marginTop: 10 }}>
            <summary className="eyebrow" style={{ cursor: 'pointer' }}>
              <Info size={12} aria-hidden /> Estimate assumptions
            </summary>
            <ul
              className="list-reset"
              style={{ marginTop: 6, display: 'flex', flexDirection: 'column', gap: 4 }}
            >
              {assumptions.map((a, i) => (
                <li key={`assumption-${i}`} style={{ fontSize: 12, color: 'var(--fg-2)' }}>
                  {a}
                </li>
              ))}
            </ul>
          </details>
        )}
      </div>

      <div className="divider" />

      <div aria-label="session-cost">
        <div className="eyebrow" style={{ marginBottom: 4 }}>
          Session · per mode
        </div>
        <div className="kv">
          <span className="k">Cascade</span>
          <span className="v">{formatUsdPerMinute(cascadePerMin)}</span>
        </div>
        <div className="kv">
          <span className="k">Realtime</span>
          <span className="v">{formatUsdPerMinute(realtimePerMin)}</span>
        </div>
      </div>

      <p className="cost-disclaimer">
        All cost figures are estimates from configured PAYG rates — not billed amounts. $/min ={' '}
        estimated cost ÷ source-speech minutes (same basis for cascade and realtime).
      </p>
    </section>
  )
}
