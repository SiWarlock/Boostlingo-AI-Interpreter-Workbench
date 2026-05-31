import { describe, expect, it } from 'vitest'
import { latencyCeilingMs, latencyTier } from './latencyTarget'

// Pins the one deterministic bit of the H.1 styling slice: the latency-vs-target tiering used by
// MetricsPanel + ComparisonSummary to color the headline latency. Thresholds anchor to the ARCH
// acceptance criteria (Realtime < 1.5s; Cascade < 3s, ideal < 2s). under → good / approaching → warn /
// past ceiling → over / null|non-finite → na, for BOTH modes.

describe('latencyTier', () => {
  it('realtime: under ideal → good, approaching → warn, past ceiling → over', () => {
    expect(latencyTier('realtime', 800)).toBe('good') // comfortably under 1.5s
    expect(latencyTier('realtime', 1200)).toBe('good') // at the ideal boundary
    expect(latencyTier('realtime', 1400)).toBe('warn') // approaching the 1.5s ceiling
    expect(latencyTier('realtime', 1500)).toBe('warn') // exactly the ceiling is not yet "over"
    expect(latencyTier('realtime', 1800)).toBe('over') // past 1.5s
  })

  it('cascade: under ideal → good, approaching → warn, past ceiling → over', () => {
    expect(latencyTier('cascade', 1500)).toBe('good') // under the 2s ideal
    expect(latencyTier('cascade', 2000)).toBe('good') // at the ideal boundary
    expect(latencyTier('cascade', 2600)).toBe('warn') // between ideal and the 3s ceiling
    expect(latencyTier('cascade', 3000)).toBe('warn') // exactly the ceiling is not yet "over"
    expect(latencyTier('cascade', 3500)).toBe('over') // past 3s
  })

  it('renders n/a for a missing or non-finite measurement (never good, never a fake 0) — both modes', () => {
    expect(latencyTier('realtime', null)).toBe('na')
    expect(latencyTier('cascade', undefined)).toBe('na')
    expect(latencyTier('realtime', Number.NaN)).toBe('na')
    expect(latencyTier('cascade', Number.POSITIVE_INFINITY)).toBe('na')
  })

  it('a negative latency tiers as n/a — never a misleading green/over badge (056 bug 3)', () => {
    // A negative responsiveness (a residual cross-clock skew, or a pre-VAD manual-stop anchor) is not a
    // valid "good" measurement: −3762 < goodUnderMs would otherwise read as GREEN. The VALUE is still
    // disclosed by deriveTurnMetrics (ARCH-013 no-clamp); the TIER/badge just gets no color.
    expect(latencyTier('cascade', -3762)).toBe('na')
    expect(latencyTier('realtime', -50)).toBe('na')
  })
})

describe('latencyCeilingMs', () => {
  it('returns the spec ceiling per mode (for the "target < Xs" pill label)', () => {
    expect(latencyCeilingMs('realtime')).toBe(1500)
    expect(latencyCeilingMs('cascade')).toBe(3000)
  })
})
