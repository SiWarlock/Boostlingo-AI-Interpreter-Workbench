// Thin interval heap sampler for the G.4 soak harness (ARCH-020 no-leak). `readHeap` (production:
// `performance.memory.usedJSHeapSize`, the Chrome demo path) is manual-smoke; the interval accumulation +
// clean stop are the TDD'd seam. The series feeds 087 `leakVerdict`. The harness must stop() this cleanly
// at teardown — a leaked interval would itself leak (and poison the no-leak measurement).

export type SoakHeapSampler = {
  start: () => void
  stop: () => void
  samples: () => number[]
}

export type SoakHeapSamplerDeps = {
  readHeap: () => number
  intervalMs: number
}

export function createHeapSampler(deps: SoakHeapSamplerDeps): SoakHeapSampler {
  const collected: number[] = []
  let handle: ReturnType<typeof setInterval> | null = null

  function start(): void {
    if (handle !== null) {
      return // idempotent — a second start can't double-sample.
    }
    handle = setInterval(() => {
      collected.push(deps.readHeap())
    }, deps.intervalMs)
  }

  function stop(): void {
    if (handle !== null) {
      clearInterval(handle)
      handle = null
    }
  }

  function samples(): number[] {
    return [...collected]
  }

  return { start, stop, samples }
}
