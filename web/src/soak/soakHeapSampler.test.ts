import { afterEach, describe, expect, it, vi } from 'vitest'
import { createHeapSampler } from './soakHeapSampler'

// Thin interval heap sampler (G.4 / ARCH-020 no-leak). `readHeap` (production: `performance.memory
// .usedJSHeapSize`, the Chrome demo path) is manual-smoke; the interval accumulation + clean stop are the
// TDD'd seam. The accumulated series feeds 087 `leakVerdict`.
describe('createHeapSampler', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('accumulates an ordered heap series on the interval; stop() clears it', () => {
    vi.useFakeTimers()
    let n = 0
    const readHeap = vi.fn(() => {
      n += 1
      return n * 1000
    })
    const sampler = createHeapSampler({ readHeap, intervalMs: 100 })

    sampler.start()
    vi.advanceTimersByTime(350) // 3 ticks at 100ms
    expect(sampler.samples()).toEqual([1000, 2000, 3000])

    sampler.stop()
    vi.advanceTimersByTime(500) // no more ticks after stop
    expect(sampler.samples()).toEqual([1000, 2000, 3000])
    expect(readHeap).toHaveBeenCalledTimes(3)
  })
})
