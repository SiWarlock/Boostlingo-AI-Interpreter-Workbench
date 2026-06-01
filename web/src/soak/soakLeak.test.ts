import { describe, expect, it } from 'vitest'
import { leakVerdict } from './soakLeak'

// ARCH-020 "no memory leak" → a deterministic verdict over an ordered `usedJSHeapSize` series
// (`performance.memory`, the Chrome demo path). Heuristic (G.4 decision 2A/Q2): discard the first
// `warmupCount` samples (startup allocation is expected), then PASS when the post-warmup trend plateaus
// and FAIL on a sustained monotonic climb past a configured per-sample threshold. The runtime SAMPLING is
// 088; the verdict MATH is here.
describe('leakVerdict', () => {
  it('plateau after warm-up → PASS; sustained climb → FAIL', () => {
    // Warm-up spike in the first 2 samples, then flat → no leak.
    const plateau = [10, 30, 100, 101, 99, 100, 102, 100]
    const pass = leakVerdict(plateau, 2, 5)
    expect(pass.pass).toBe(true)
    expect(Math.abs(pass.slopeBytesPerSample)).toBeLessThan(5)

    // After warm-up the heap keeps climbing ~50/sample → a leak.
    const climb = [10, 30, 100, 150, 200, 250, 300, 350]
    const fail = leakVerdict(climb, 2, 5)
    expect(fail.pass).toBe(false)
    expect(fail.slopeBytesPerSample).toBeGreaterThan(5)
  })

  it('insufficient post-warmup samples → slope 0 → PASS (no leak evidence)', () => {
    const v = leakVerdict([10, 20, 30], 2, 5)
    expect(v.slopeBytesPerSample).toBe(0)
    expect(v.pass).toBe(true)
    expect(v.sampleCount).toBe(1)
  })
})
