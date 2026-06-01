import { describe, expect, it } from 'vitest'
import { assembleSoakReport } from './soakReport'
import type { SoakReportInputs } from './soakReport'

// The structured SoakReport is the deliverable artifact for one mode's 5-min run. Its three ARCH-020
// booleans are DERIVED (not asserted by hand) from the computed verdicts + disconnect count:
//   noDisconnect  = disconnectCount === 0
//   noDriftOverlap = latency verdict PASS  AND  no overlaps
//   noLeak        = heap-leak verdict PASS
// so the §15 three-check gate can't drift from the underlying measurements.
function baseInputs(): SoakReportInputs {
  return {
    mode: 'cascade',
    durationSec: 300,
    turnCount: 24,
    disconnectCount: 0,
    latency: { slopeMsPerTurn: 1, thresholdMsPerTurn: 50, pass: true },
    overlaps: [],
    skewSlope: 0.001,
    heapLeak: { slopeBytesPerSample: 2, thresholdBytesPerSample: 5, sampleCount: 60, pass: true },
    werSummary: { meanWer: 0.1, medianWer: 0.08, count: 24 },
    overlapMeasured: true,
    overlapBasis: 'token-derived',
  }
}

describe('assembleSoakReport', () => {
  it('derives the three ARCH-020 booleans from the verdicts + disconnect count', () => {
    const report = assembleSoakReport(baseInputs())
    expect(report.arch020).toEqual({ noDisconnect: true, noDriftOverlap: true, noLeak: true })
    expect(report.mode).toBe('cascade')
    expect(report.turnCount).toBe(24)
    expect(report.durationSec).toBe(300)
  })

  it('flips noDisconnect when a transport closed mid-run', () => {
    const report = assembleSoakReport({ ...baseInputs(), disconnectCount: 1 })
    expect(report.arch020.noDisconnect).toBe(false)
  })

  it('flips noDriftOverlap on a failed latency verdict OR any overlap', () => {
    const onSlope = assembleSoakReport({
      ...baseInputs(),
      latency: { slopeMsPerTurn: 120, thresholdMsPerTurn: 50, pass: false },
    })
    expect(onSlope.arch020.noDriftOverlap).toBe(false)

    const onOverlap = assembleSoakReport({
      ...baseInputs(),
      overlaps: [{ prevIndex: 3, nextIndex: 4, overshootMs: 200 }],
    })
    expect(onOverlap.arch020.noDriftOverlap).toBe(false)
  })

  it('flips noLeak on a failed heap-leak verdict', () => {
    const report = assembleSoakReport({
      ...baseInputs(),
      heapLeak: {
        slopeBytesPerSample: 99,
        thresholdBytesPerSample: 5,
        sampleCount: 60,
        pass: false,
      },
    })
    expect(report.arch020.noLeak).toBe(false)
  })

  // overlapMeasured DISCLOSES whether overlap was actually checked — with no per-turn output-audio
  // duration every playbackEndMs is null, detectOverlaps returns [], and noDriftOverlap would otherwise
  // silently read as "checked, none found". The report must tell the truth (honest-degrade posture).
  it('carries the overlapMeasured disclosure through', () => {
    expect(assembleSoakReport(baseInputs()).overlapMeasured).toBe(true)
    expect(assembleSoakReport({ ...baseInputs(), overlapMeasured: false }).overlapMeasured).toBe(
      false,
    )
  })

  // overlapBasis discloses HOW the overlap duration was derived per mode — realtime is token-derived
  // (precise, reported usage) vs cascade char-estimate (rougher, the disclosed §36 cost basis) — so a
  // cascade overlapMeasured:true isn't read as an exact measurement (093 TWEAK).
  it('carries the per-mode overlapBasis disclosure through', () => {
    expect(assembleSoakReport(baseInputs()).overlapBasis).toBe('token-derived')
    expect(
      assembleSoakReport({ ...baseInputs(), overlapBasis: 'char-estimate' }).overlapBasis,
    ).toBe('char-estimate')
  })
})
