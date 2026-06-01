import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSoakRunner } from './soakRunner'
import type { SoakTurnObservation } from './soakRunner'
import { createHeapSampler } from './soakHeapSampler'
import { computeSchedule } from './soakSchedule'
import { CANONICAL_SOAK_SCRIPT } from './soakScript'

// The soak RUNNER orchestration (G.4 / ARCH-020). All side-effects are injected seams (drive, synthetic
// stream, store-read, heap sampler, WER, endSession) so the orchestration is TDD'd with fake deps + fake
// timers; the LIVE composition of those seams (synthetic getUserMedia → real recordingActions/
// realtimeTurnController → real providers) is the manual-smoke shell. The runner: start sampler → drive
// the mode → play the synthetic stream → wait the run duration → collect per-turn series FROM THE STORE
// (no bypass, ARCH-007) → pair script↔transcript for WER → assemble the SoakReport (087) → tear down.

// Fresh spy-backed deps; reconfigure individual fields per test.
function makeDeps() {
  return {
    script: CANONICAL_SOAK_SCRIPT,
    schedule: computeSchedule([1000, 1000], 500), // offsets 0, 1500
    drive: {
      start: vi.fn().mockResolvedValue(undefined),
      stop: vi.fn().mockResolvedValue(undefined),
      disconnectCount: vi.fn().mockReturnValue(0),
    },
    syntheticStream: { start: vi.fn(), stop: vi.fn() },
    store: { getCompletedTurns: vi.fn(() => [] as SoakTurnObservation[]) },
    heapSampler: { start: vi.fn(), stop: vi.fn(), samples: vi.fn(() => [] as number[]) },
    computeWer: vi.fn().mockResolvedValue(0.1),
    endSession: vi.fn().mockResolvedValue(undefined),
    config: {
      durationMs: 1000,
      driftThresholdMsPerTurn: 50,
      leakWarmupCount: 0,
      leakThresholdBytesPerSample: 1000,
    },
  }
}

afterEach(() => {
  vi.useRealTimers()
})

describe('createSoakRunner', () => {
  it('drives the selected mode and starts the synthetic stream', async () => {
    vi.useFakeTimers()
    const deps = makeDeps()
    const p = createSoakRunner(deps).run('realtime')
    await vi.advanceTimersByTimeAsync(deps.config.durationMs)
    await p

    expect(deps.drive.start).toHaveBeenCalledWith('realtime')
    expect(deps.syntheticStream.start).toHaveBeenCalledTimes(1)
  })

  it('samples heap on an interval across the run duration', async () => {
    vi.useFakeTimers()
    const readHeap = vi.fn().mockReturnValue(1000)
    // Inject a REAL heap sampler (the spy makeDeps() one can't sample on a timer) over the rest of the fakes.
    const deps = { ...makeDeps(), heapSampler: createHeapSampler({ readHeap, intervalMs: 100 }) }
    deps.config.durationMs = 350
    const p = createSoakRunner(deps).run('cascade')
    await vi.advanceTimersByTimeAsync(350)
    await p

    // ~3 ticks at 100ms across the 350ms run (sampler started at run-start, stopped at teardown).
    expect(readHeap.mock.calls.length).toBeGreaterThanOrEqual(3)
  })

  it('collects the per-turn latency series + playback-end stamps from the store in order (no bypass)', async () => {
    vi.useFakeTimers()
    const deps = makeDeps()
    deps.schedule = computeSchedule([1000, 1000], 500) // offsets 0, 1500
    // Rising latency → positive slope → drift FAIL; turn 0 plays past turn 1's scheduled start → overlap.
    deps.store.getCompletedTurns.mockReturnValue([
      { index: 0, endToEndLatencyMs: 100, playbackEndMs: 2000, sourceTranscript: 'a' },
      { index: 1, endToEndLatencyMs: 900, playbackEndMs: 2500, sourceTranscript: 'b' },
    ])
    const report = await runWithTimers(deps, 'cascade')

    expect(report.turnCount).toBe(2)
    expect(report.latency.slopeMsPerTurn).toBeCloseTo(800, 5) // 100→900 over index 0→1
    expect(report.latency.pass).toBe(false) // 800 > threshold 50
    expect(report.overlaps).toEqual([{ prevIndex: 0, nextIndex: 1, overshootMs: 500 }]) // 2000 > 1500
    expect(report.arch020.noDriftOverlap).toBe(false)
  })

  it('pairs each turn transcript (hypothesis) with its script utterance (reference) for WER, in order', async () => {
    vi.useFakeTimers()
    const deps = makeDeps()
    deps.store.getCompletedTurns.mockReturnValue([
      { index: 0, endToEndLatencyMs: 500, playbackEndMs: 100, sourceTranscript: 'HYP-0' },
      { index: 1, endToEndLatencyMs: 500, playbackEndMs: 200, sourceTranscript: 'HYP-1' },
    ])
    deps.computeWer.mockResolvedValue(0.25)
    const report = await runWithTimers(deps, 'cascade')

    expect(deps.computeWer).toHaveBeenCalledTimes(2)
    expect(deps.computeWer).toHaveBeenNthCalledWith(
      1,
      CANONICAL_SOAK_SCRIPT.utterances[0].text,
      'HYP-0',
    )
    expect(deps.computeWer).toHaveBeenNthCalledWith(
      2,
      CANONICAL_SOAK_SCRIPT.utterances[1].text,
      'HYP-1',
    )
    expect(report.werSummary.count).toBe(2)
    expect(report.werSummary.meanWer).toBeCloseTo(0.25, 6)
  })

  it('assembles a SoakReport whose three ARCH-020 booleans derive from the collected verdicts', async () => {
    vi.useFakeTimers()
    const deps = makeDeps()
    deps.schedule = computeSchedule([1000, 1000, 1000], 500) // offsets 0, 1500, 3000
    deps.store.getCompletedTurns.mockReturnValue([
      { index: 0, endToEndLatencyMs: 500, playbackEndMs: 900, sourceTranscript: 'hello' },
      { index: 1, endToEndLatencyMs: 510, playbackEndMs: 2400, sourceTranscript: 'hola' },
      { index: 2, endToEndLatencyMs: 505, playbackEndMs: 3900, sourceTranscript: 'gracias' },
    ])
    deps.heapSampler.samples.mockReturnValue([100, 100, 100]) // plateau → no leak
    const report = await runWithTimers(deps, 'cascade')

    expect(report.mode).toBe('cascade')
    expect(report.turnCount).toBe(3)
    expect(report.durationSec).toBe(1)
    expect(report.arch020).toEqual({ noDisconnect: true, noDriftOverlap: true, noLeak: true })
    expect(report.werSummary.count).toBe(3)
    expect(report.overlapMeasured).toBe(true) // finite playback-end stamps → overlap WAS checked
  })

  it('discloses overlapMeasured=false when no turn carries a playback-end stamp', async () => {
    vi.useFakeTimers()
    const deps = makeDeps()
    deps.store.getCompletedTurns.mockReturnValue([
      { index: 0, endToEndLatencyMs: 500, playbackEndMs: null, sourceTranscript: 'a' },
    ])
    const report = await runWithTimers(deps, 'cascade')
    // No playback-end stamp → detectOverlaps can't run → disclose unmeasured, not a silent clean pass.
    expect(report.overlapMeasured).toBe(false)
  })

  it('discloses the per-mode overlapBasis (realtime token-derived / cascade char-estimate / none unmeasured)', async () => {
    vi.useFakeTimers()
    const measured: SoakTurnObservation[] = [
      { index: 0, endToEndLatencyMs: 500, playbackEndMs: 900, sourceTranscript: 'a' },
    ]

    const rt = makeDeps()
    rt.store.getCompletedTurns.mockReturnValue(measured)
    expect((await runWithTimers(rt, 'realtime')).overlapBasis).toBe('token-derived')

    const cas = makeDeps()
    cas.store.getCompletedTurns.mockReturnValue(measured)
    expect((await runWithTimers(cas, 'cascade')).overlapBasis).toBe('char-estimate')

    // No measured overlap → basis 'none' (not a mode-claim of measurement).
    expect((await runWithTimers(makeDeps(), 'cascade')).overlapBasis).toBe('none')
  })

  it('tears down cleanly at the end of the run (stream, drive, sampler, session)', async () => {
    vi.useFakeTimers()
    const deps = makeDeps()
    await runWithTimers(deps, 'cascade')

    expect(deps.syntheticStream.stop).toHaveBeenCalledTimes(1)
    expect(deps.drive.stop).toHaveBeenCalledTimes(1)
    expect(deps.heapSampler.stop).toHaveBeenCalledTimes(1)
    expect(deps.endSession).toHaveBeenCalledTimes(1)
  })
})

// Drive a run to completion under fake timers (advance past the duration, flush the async collection).
async function runWithTimers(deps: ReturnType<typeof makeDeps>, mode: 'cascade' | 'realtime') {
  const p = createSoakRunner(deps).run(mode)
  await vi.advanceTimersByTimeAsync(deps.config.durationMs)
  return p
}
