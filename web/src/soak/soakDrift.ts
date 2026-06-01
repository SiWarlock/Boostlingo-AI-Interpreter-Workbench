import type { SoakSchedule } from './soakSchedule'

// Audio-drift measurement math (G.4 decisions 2A + 2C). All pure — they consume the metrics trail the
// workbench already produces (per-turn LatencyEvents, playback stamps) + the harness's deterministic
// schedule. Primary drift signal = the latency SLOPE + unplanned overlap; secondary = playback-clock skew.

// Shared least-squares slope of ys over xs. n < 2 (can't regress) or a degenerate x-spread → 0 (no trend
// evidence). Used by latencySlope (x = turn index), playbackSkewSlope (x = wall-clock), and — via
// latencySlope — soakLeak's heap-vs-sample slope.
function regressionSlope(xs: number[], ys: number[]): number {
  const n = xs.length
  if (n < 2) {
    return 0
  }
  let sumX = 0
  let sumY = 0
  for (let i = 0; i < n; i++) {
    sumX += xs[i]
    sumY += ys[i]
  }
  const meanX = sumX / n
  const meanY = sumY / n
  let numerator = 0
  let denominator = 0
  for (let i = 0; i < n; i++) {
    const dx = xs[i] - meanX
    numerator += dx * (ys[i] - meanY)
    denominator += dx * dx
  }
  return denominator === 0 ? 0 : numerator / denominator
}

// Slope of the per-turn end-to-end latency series over the turn index (ms/turn). Flat ⇒ no accumulating lag.
export function latencySlope(series: number[]): number {
  const xs = series.map((_, i) => i)
  return regressionSlope(xs, series)
}

export type DriftVerdict = {
  slopeMsPerTurn: number
  thresholdMsPerTurn: number
  pass: boolean
}

// ONE-SIDED gate (decision 2A — drift = accumulating lag): FAIL only on an upward trend past the threshold;
// a flat or negative/improving slope PASSES (early-turn cold-connection latency relaxing over the run is not
// a stability failure). The raw `slopeMsPerTurn` is surfaced two-sided so a large negative swing stays
// visible for inspection — the gate is one-sided, the signal isn't.
export function driftVerdict(series: number[], thresholdMsPerTurn: number): DriftVerdict {
  const slopeMsPerTurn = latencySlope(series)
  return { slopeMsPerTurn, thresholdMsPerTurn, pass: slopeMsPerTurn <= thresholdMsPerTurn }
}

export type Overlap = {
  prevIndex: number
  nextIndex: number
  overshootMs: number
}

// Unplanned overlap: turn N's actual playback runs past turn N+1's SCHEDULED injection start (decision 2A).
// `playbackEndsMs[i]` is turn i's playback-complete stamp on the same run-relative clock as the schedule
// offsets (088 supplies both on one origin). A missing/non-finite stamp is skipped (that turn isn't checked).
export function detectOverlaps(schedule: SoakSchedule, playbackEndsMs: number[]): Overlap[] {
  const overlaps: Overlap[] = []
  const { utterances } = schedule
  for (let i = 0; i < utterances.length - 1; i++) {
    const prevEnd = playbackEndsMs[i]
    if (typeof prevEnd !== 'number' || !Number.isFinite(prevEnd)) {
      continue
    }
    const nextStart = utterances[i + 1].startOffsetMs
    if (prevEnd > nextStart) {
      overlaps.push({ prevIndex: i, nextIndex: i + 1, overshootMs: prevEnd - nextStart })
    }
  }
  return overlaps
}

export type PlaybackSkewSample = {
  audioClockMs: number
  wallClockMs: number
}

// Secondary signal (decision 2C): the slope of (audioClock − wallClock) over wall-clock time — the rate the
// playback clock drifts from real time (0 ⇒ perfect tracking; buffer under/overrun accumulates a non-zero slope).
export function playbackSkewSlope(samples: PlaybackSkewSample[]): number {
  const xs = samples.map((s) => s.wallClockMs)
  const ys = samples.map((s) => s.audioClockMs - s.wallClockMs)
  return regressionSlope(xs, ys)
}
