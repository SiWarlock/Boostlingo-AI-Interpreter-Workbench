import { describe, expect, it } from 'vitest'
import { detectOverlaps, driftVerdict, latencySlope, playbackSkewSlope } from './soakDrift'
import { computeSchedule } from './soakSchedule'

// Drift = does latency/buffering ACCUMULATE over the 5-min run (G.4 decision 2A). The primary signal is
// the linear-regression SLOPE of per-turn end-to-end latency: flat ⇒ no accumulating lag (PASS); a sustained
// rise ⇒ the ARCH-020 drift failure (FAIL). The verdict is ONE-SIDED — only an UPWARD trend fails; a flat or
// negative/improving slope PASSES (early-turn cold-connection latency relaxing over the run is NOT a failure).
// The raw `slopeMsPerTurn` stays in the verdict so a large negative swing is still visible for inspection.
// Threshold is a configured constant (ms/turn) — the test pins the math, the live 088 run calibrates it (Q1).
describe('latencySlope + driftVerdict', () => {
  it('flat latency → slope ~0 → PASS; rising latency → positive slope → FAIL', () => {
    const flat = [500, 510, 495, 505, 500]
    expect(Math.abs(latencySlope(flat))).toBeLessThan(5)
    expect(driftVerdict(flat, 50).pass).toBe(true)

    const rising = [500, 600, 700, 800, 900] // +100 ms / turn — accumulating lag.
    expect(latencySlope(rising)).toBeCloseTo(100, 5)
    const verdict = driftVerdict(rising, 50)
    expect(verdict.pass).toBe(false)
    expect(verdict.slopeMsPerTurn).toBeCloseTo(100, 5)
  })

  it('clearly-negative slope (latency improving over the run) → PASS (one-sided gate)', () => {
    const falling = [900, 800, 700, 600, 500] // −100 ms / turn — improving, NOT a drift failure.
    const verdict = driftVerdict(falling, 50)
    expect(verdict.slopeMsPerTurn).toBeCloseTo(-100, 5) // raw slope stays two-sided / visible.
    expect(verdict.pass).toBe(true) // gate is one-sided: slope ≤ threshold.
  })
})

// Overlap = turn N+1's scheduled injection begins before turn N's output playback completes when the
// script says it shouldn't (G.4 decision 2A). Reads the deterministic schedule offsets vs the per-turn
// playback-complete stamps and reports the offending pairs — an unplanned collision is a drift failure.
describe('detectOverlaps', () => {
  it('flags turn pairs whose prior playback runs past the next scheduled injection', () => {
    const schedule = computeSchedule([1000, 1500], 500) // start offsets [0, 1500]
    // turn 0 plays until 2000 (> turn 1's start 1500) → unplanned overlap, overshoot 500 ms.
    const overlaps = detectOverlaps(schedule, [2000, 3200])
    expect(overlaps).toEqual([{ prevIndex: 0, nextIndex: 1, overshootMs: 500 }])
  })

  it('reports no overlap for a clean schedule', () => {
    const schedule = computeSchedule([1000, 1500], 500)
    // turn 0 finishes at 1000 ≤ turn 1's start 1500 — no collision.
    expect(detectOverlaps(schedule, [1000, 3000])).toEqual([])
  })
})

// Playback-clock vs wall-clock skew (G.4 decision 2C) — a cheap SECONDARY signal catching playback-buffer
// underrun/overrun accumulation. skew = audioClock − wallClock; the slope of skew over wall-clock time is
// the rate it accumulates (0 ⇒ the playback clock tracks real time).
describe('playbackSkewSlope', () => {
  it('computes the slope of (audioClock - wallClock) over wall-clock time', () => {
    // Audio clock runs 10 ms fast per 1000 ms of wall-clock → skew slope 0.01 ms/ms.
    const samples = [
      { wallClockMs: 0, audioClockMs: 0 },
      { wallClockMs: 1000, audioClockMs: 1010 },
      { wallClockMs: 2000, audioClockMs: 2020 },
      { wallClockMs: 3000, audioClockMs: 3030 },
    ]
    expect(playbackSkewSlope(samples)).toBeCloseTo(0.01, 6)
  })

  it('returns 0 skew slope for a perfectly tracking clock', () => {
    const samples = [
      { wallClockMs: 0, audioClockMs: 0 },
      { wallClockMs: 1000, audioClockMs: 1000 },
      { wallClockMs: 2000, audioClockMs: 2000 },
    ]
    expect(playbackSkewSlope(samples)).toBeCloseTo(0, 6)
  })
})
