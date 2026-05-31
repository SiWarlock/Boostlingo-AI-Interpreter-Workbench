import type { InterpretationMode } from '../types/domain'

// Pure latency-vs-target tiering for the headline turn latency (ARCH-013 universal metrics +
// the final-submission criteria). Display-only, like errorCopy — it maps a measured latency to a
// good/warn/over/na tier the panels color with. No store/contract/behavior; unit-tested here because
// it is the one deterministic bit of the otherwise manual-smoke H.1 slice.
//
// Targets (ms) — the spec ceilings the UI colors against:
//   - Realtime: speech-end → first audio < 1.5s (ARCH-010 / acceptance criteria).
//   - Cascade:  end-to-end < 3s, ideal < 2s (ARCH-011 / acceptance criteria).
// The `ceilingMs` is the spec target (the pill label "target < Xs" + the good→over boundary);
// `goodUnderMs` flags "comfortably under" (green) vs "approaching/over-ideal but under ceiling" (amber).
const TARGETS: Record<InterpretationMode, { goodUnderMs: number; ceilingMs: number }> = {
  realtime: { goodUnderMs: 1200, ceilingMs: 1500 },
  cascade: { goodUnderMs: 2000, ceilingMs: 3000 },
}

export type LatencyTier = 'good' | 'warn' | 'over' | 'na'

// The spec ceiling for a mode (ms) — used for the "target < Xs" pill label.
export function latencyCeilingMs(mode: InterpretationMode): number {
  return TARGETS[mode].ceilingMs
}

// good = comfortably under the ideal; warn = under the ceiling but over the ideal; over = past the
// ceiling; na = no / non-finite / NEGATIVE measurement (a missing metric renders muted 'n/a', never green,
// never an error color — and never a fabricated 0; mirrors the comparison aggregation's Number.isFinite
// guard, web §21 — NaN/Infinity must not read as "good"). A negative latency (a residual cross-clock skew,
// or a pre-VAD manual-stop anchor — 056 bug 3) is not a valid "good" measurement → 'na' (no misleading
// green/over badge); the VALUE is still disclosed by deriveTurnMetrics (ARCH-013 no-clamp), only the badge mutes.
export function latencyTier(mode: InterpretationMode, ms: number | null | undefined): LatencyTier {
  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) {
    return 'na'
  }
  const { goodUnderMs, ceilingMs } = TARGETS[mode]
  if (ms <= goodUnderMs) {
    return 'good'
  }
  if (ms <= ceilingMs) {
    return 'warn'
  }
  return 'over'
}
