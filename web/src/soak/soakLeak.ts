import { latencySlope } from './soakDrift'

// Memory-leak verdict (ARCH-020 "no memory leak"). Pure math over an ordered `usedJSHeapSize` series
// (sampled from `performance.memory` at runtime/088 — the Chrome demo path). Heuristic (decision 2A/Q2):
// discard the first `warmupCount` samples (startup allocation is expected, not a leak), then take the
// slope of the remainder over the sample index (bytes/sample, reusing soakDrift's index regression). The
// gate is ONE-SIDED — only an upward climb past the threshold fails (a leak only grows).

export type LeakVerdict = {
  slopeBytesPerSample: number
  thresholdBytesPerSample: number
  sampleCount: number
  pass: boolean
}

export function leakVerdict(
  heapSamples: number[],
  warmupCount: number,
  thresholdBytesPerSample: number,
): LeakVerdict {
  const postWarmup = heapSamples.slice(Math.max(0, warmupCount))
  const sampleCount = postWarmup.length
  // < 2 post-warmup samples can't establish a trend → slope 0 (no leak evidence), PASS.
  const slopeBytesPerSample = sampleCount >= 2 ? latencySlope(postWarmup) : 0
  return {
    slopeBytesPerSample,
    thresholdBytesPerSample,
    sampleCount,
    pass: slopeBytesPerSample <= thresholdBytesPerSample,
  }
}
