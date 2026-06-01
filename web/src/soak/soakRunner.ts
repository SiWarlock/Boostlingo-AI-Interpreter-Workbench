import type { InterpretationMode } from '../types/domain'
import type { SoakSchedule } from './soakSchedule'
import type { SoakScript } from './soakScript'
import type { SyntheticAudioStream } from './syntheticAudioStream'
import type { SoakHeapSampler } from './soakHeapSampler'
import { detectOverlaps, driftVerdict, playbackSkewSlope } from './soakDrift'
import type { PlaybackSkewSample } from './soakDrift'
import { leakVerdict } from './soakLeak'
import { aggregateWer } from './soakWer'
import type { SoakWerTurn } from './soakWer'
import { assembleSoakReport } from './soakReport'
import type { SoakReport } from './soakReport'

// The soak RUNNER orchestration (G.4 / ARCH-020). All side-effects are injected SEAMS so the orchestration
// is TDD'd with fake deps + fake timers; the LIVE composition of those seams (synthetic getUserMedia → the
// real recordingActions/realtimeTurnController → real providers) is the 089b smoke shell. The runner reads
// completed turns FROM THE STORE (no bypass, ARCH-007) and assembles the SoakReport via the 087 engine.

// One completed turn's soak-relevant projection. 089b's adapter derives these from the real store
// `turns[]` (end-to-end via deriveTurnMetrics.speechEndToFirstAudioMs; playbackEndMs = run-relative
// playback END; the final source transcript). Here it is the abstract collection seam.
export type SoakTurnObservation = {
  index: number
  endToEndLatencyMs: number | null
  playbackEndMs: number | null
  sourceTranscript: string
}

export type SoakStoreView = {
  getCompletedTurns: () => SoakTurnObservation[]
}

// The live-drive seam (089b composes it from the real capture/realtime stack). `disconnectCount` rides the
// existing client close / pc-connectionstate callbacks.
export type SoakDrive = {
  start: (mode: InterpretationMode) => Promise<void>
  stop: () => Promise<void> | void
  disconnectCount: () => number
}

export type SoakRunConfig = {
  durationMs: number
  driftThresholdMsPerTurn: number
  leakWarmupCount: number
  leakThresholdBytesPerSample: number
}

export type SoakRunnerDeps = {
  script: SoakScript
  schedule: SoakSchedule
  drive: SoakDrive
  syntheticStream: Pick<SyntheticAudioStream, 'start' | 'stop'>
  store: SoakStoreView
  heapSampler: SoakHeapSampler
  computeWer: (reference: string, hypothesis: string) => Promise<number>
  endSession: () => Promise<void> | void
  config: SoakRunConfig
  // Optional secondary playback-skew samples (089b populates; absent → skew slope 0).
  skewSamples?: () => PlaybackSkewSample[]
}

export type SoakRunner = {
  run: (mode: InterpretationMode) => Promise<SoakReport>
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export function createSoakRunner(deps: SoakRunnerDeps): SoakRunner {
  async function run(mode: InterpretationMode): Promise<SoakReport> {
    // Drive the live flow + play the synthetic conversation while sampling heap, for the run duration.
    deps.heapSampler.start()
    await deps.drive.start(mode)
    deps.syntheticStream.start()
    await delay(deps.config.durationMs)

    // Teardown — the harness must not itself leak (a leaked timer/track/stream poisons the no-leak
    // measurement). Stop the synthetic stream, the drive, the sampler, and end the session.
    deps.syntheticStream.stop()
    await deps.drive.stop()
    deps.heapSampler.stop()
    await deps.endSession()

    // Collect per-turn observations from the store (no bypass, ARCH-007), in order.
    const turns = deps.store.getCompletedTurns()
    const latencySeries = turns
      .map((t) => t.endToEndLatencyMs)
      .filter((v): v is number => v !== null && Number.isFinite(v))
    // NaN for a missing playback-end stamp — detectOverlaps skips non-finite (that turn isn't checked).
    const playbackEndsMs = turns.map((t) => t.playbackEndMs ?? Number.NaN)

    // WER-via-script: pair each turn's source transcript (hypothesis) with its script utterance
    // (reference) by index — the hypothesis is the REAL pipeline STT (Finding-3 sidestep).
    const werTurns: SoakWerTurn[] = []
    for (let i = 0; i < turns.length; i++) {
      const reference = deps.script.utterances[i]?.text ?? ''
      const hypothesis = turns[i].sourceTranscript
      if (reference === '' || hypothesis === '') {
        continue
      }
      const werValue = await deps.computeWer(reference, hypothesis)
      werTurns.push({ referenceText: reference, werValue })
    }

    const skewSamples = deps.skewSamples?.() ?? []

    return assembleSoakReport({
      mode,
      durationSec: deps.config.durationMs / 1000,
      turnCount: turns.length,
      disconnectCount: deps.drive.disconnectCount(),
      latency: driftVerdict(latencySeries, deps.config.driftThresholdMsPerTurn),
      overlaps: detectOverlaps(deps.schedule, playbackEndsMs),
      skewSlope: playbackSkewSlope(skewSamples),
      heapLeak: leakVerdict(
        deps.heapSampler.samples(),
        deps.config.leakWarmupCount,
        deps.config.leakThresholdBytesPerSample,
      ),
      werSummary: aggregateWer(werTurns),
    })
  }

  return { run }
}

export function runSoak(mode: InterpretationMode, deps: SoakRunnerDeps): Promise<SoakReport> {
  return createSoakRunner(deps).run(mode)
}
