import type { InterpretationMode } from '../types/domain'
import { computeSchedule } from './soakSchedule'
import type { SoakSchedule } from './soakSchedule'
import type { SoakDrive } from './soakRunner'
import { runSoak } from './soakRunner'
import type { SoakReport } from './soakReport'
import { createSyntheticAudioStream } from './syntheticAudioStream'
import { createSoakAudioCache } from './soakAudioCache'
import { createHeapSampler } from './soakHeapSampler'
import { createSoakStoreView } from './soakStoreView'
import { createSoakWer } from './soakWerClient'
import { CANONICAL_SOAK_SCRIPT } from './soakScript'
import { createAudioCaptureController } from '../audio/audioCaptureController'
import { createRecordingController } from '../state/recordingActions'
import { createRealtimeWebRtcClient } from '../realtime/realtimeWebRtcClient'
import { createRealtimeConnectionManager } from '../realtime/realtimeConnectionManager'
import { createRealtimeTurnController } from '../realtime/realtimeTurnController'
import { cascadeStreamClient, setOnTerminal } from '../cascade/cascadeStreamClient'
import { sessionStore } from '../state/sessionStore'
import { sessionsApi } from '../api/sessionsApi'
import { realtimeApi } from '../api/realtimeApi'

// `composeSoakDrive` (089b) — the live composition that satisfies the 089a `SoakDrive` seam. MOSTLY SMOKE
// (real browser audio + real capture/realtime clients + the real drive), validated at the manual real-key
// run. Its one deterministic, correctness-critical bit — index-aligning decoded buffer durations to the
// 087 schedule — is TDD'd (`buildSoakScheduleFromBuffers`).

// AudioBuffer.duration is in SECONDS → ms; the resulting schedule is index-aligned to the buffers (a wrong
// alignment desyncs the whole conversation), so the synthetic stream plays each utterance at its offset.
export function buildSoakScheduleFromBuffers(buffers: AudioBuffer[], gapMs: number): SoakSchedule {
  return computeSchedule(
    buffers.map((buffer) => buffer.duration * 1000),
    gapMs,
  )
}

// ─── Smoke below this line (manual-run validated) ────────────────────────────────────────────────────

// Soak run constants (Q4 — constants for a quick launch; the manual run overrides if needed).
const HEAP_INTERVAL_MS = 1000
const DRAIN_MARGIN_MS = 5000
const TARGET_DURATION_MS = 5 * 60 * 1000
const DRIFT_THRESHOLD_MS_PER_TURN = 50
const LEAK_WARMUP_SAMPLES = 3
const LEAK_THRESHOLD_BYTES_PER_SAMPLE = 200_000

export type ComposeSoakDriveDeps = {
  // The synthetic MediaStream supplier (= () => Promise.resolve(syntheticStream.stream)) — injected at each
  // controller's getUserMedia so the worklet/track run on the synthetic audio (decision 1A).
  getUserMedia: () => Promise<MediaStream>
}

// Build the `SoakDrive` for a mode: construct OWN capture controller / realtime client with the synthetic
// getUserMedia (zero production-singleton mutation, Q1a), and drive the real recordingActions (cascade) /
// realtimeTurnController (realtime). The store is pre-set to bidirectional + auto by the composition root.
export function composeSoakDrive(deps: ComposeSoakDriveDeps): SoakDrive {
  let stopFn: (() => void) | null = null

  async function start(mode: InterpretationMode): Promise<void> {
    if (mode === 'cascade') {
      const capture = createAudioCaptureController({ getUserMedia: deps.getUserMedia })
      const recording = createRecordingController({
        store: sessionStore,
        createTurn: (sessionId) => sessionsApi.createTurn(sessionId),
        client: cascadeStreamClient,
        capture,
      })
      // Re-point the cascade terminal hook to THIS controller for the run (the J.5 continuous re-arm).
      setOnTerminal(() => recording.onCascadeTerminal())
      await recording.startRecording()
      stopFn = () => recording.stopRecording()
    } else {
      const clock = () => new Date().toISOString()
      const client = createRealtimeWebRtcClient({
        mint: () => {
          const state = sessionStore.getState()
          return realtimeApi.mintClientSecret({
            sessionId: state.sessionId ?? '',
            direction: state.direction,
            model: state.realtimeModel,
            bidirectional: state.bidirectional,
          })
        },
        onRemoteTrack: () => {}, // the soak doesn't need to render output audio
        getUserMedia: deps.getUserMedia,
      })
      const connectionManager = createRealtimeConnectionManager({
        store: sessionStore,
        client,
        clock,
      })
      const controller = createRealtimeTurnController({
        store: sessionStore,
        client,
        connectionManager,
        api: {
          createTurn: (sessionId) => sessionsApi.createTurn(sessionId),
          appendTurnEvents: (sessionId, turnId, events) =>
            sessionsApi.appendTurnEvents(sessionId, turnId, events),
          completeTurn: (sessionId, turnId, body) =>
            sessionsApi.completeTurn(sessionId, turnId, body),
        },
        clock,
      })
      await controller.startTurn()
      stopFn = () => controller.stopTurn()
    }
  }

  function stop(): void {
    stopFn?.()
    stopFn = null
  }

  // Smoke proxy: a transport disconnect manifests as a failed turn (the precise WS-close / pc-failed hook is
  // a manual-run observation). Counts failed turns in the store.
  function disconnectCount(): number {
    return sessionStore.getState().turns.filter((turn) => turn.status === 'failed').length
  }

  return { start, stop, disconnectCount }
}

// `performance.memory` is Chrome-only (not in the TS DOM lib) — the documented Chrome demo path.
function readJsHeap(): number {
  const perf = performance as Performance & { memory?: { usedJSHeapSize: number } }
  return perf.memory?.usedJSHeapSize ?? 0
}

// The composition ROOT (smoke): synthesize+cache the script audio, build the synthetic stream, create a
// bidirectional+auto session, compose the drive + the runner deps, run, and tear down. Driven by the dev
// `?soak=1` panel. Validated end-to-end at the manual real-key run (real TTS → decodable audio).
export async function runSoakHarness(mode: InterpretationMode): Promise<SoakReport> {
  const context = new AudioContext()
  const cache = createSoakAudioCache({ decodeAudio: (bytes) => context.decodeAudioData(bytes) })

  const buffers: AudioBuffer[] = []
  for (const utterance of CANONICAL_SOAK_SCRIPT.utterances) {
    buffers.push(await cache.load(utterance.text, utterance.sourceLang))
  }
  const schedule = buildSoakScheduleFromBuffers(buffers, CANONICAL_SOAK_SCRIPT.gapMs)
  const synthetic = createSyntheticAudioStream({ context, buffers, schedule })

  // Configure + create the session (bidirectional + auto-VAD continuous).
  sessionStore.updateSessionConfig({ mode, bidirectional: true })
  sessionStore.setTurnControlMode('auto')
  const created = await sessionsApi.createSession({
    label: `soak-${mode}`,
    mode,
    direction: sessionStore.getState().direction,
    realtimeModel: sessionStore.getState().realtimeModel,
    translationModel: sessionStore.getState().translationModel,
  })
  sessionStore.sessionStarted(created)
  const sessionId = sessionStore.getState().sessionId ?? ''
  const runStartMs = Date.now()

  const drive = composeSoakDrive({ getUserMedia: () => Promise.resolve(synthetic.stream) })
  const report = await runSoak(mode, {
    script: CANONICAL_SOAK_SCRIPT,
    schedule,
    drive,
    syntheticStream: synthetic,
    store: createSoakStoreView({
      getTurns: () => sessionStore.getState().turns,
      runStartMs,
      // No reliable per-turn output-audio duration in the store → playbackEndMs null → overlap is
      // disclosed-unmeasured (honest; the latency-slope drift is the primary signal).
      resolveOutputDurationMs: () => null,
    }),
    heapSampler: createHeapSampler({ readHeap: readJsHeap, intervalMs: HEAP_INTERVAL_MS }),
    computeWer: createSoakWer(sessionId),
    endSession: async () => {
      if (sessionId !== '') {
        try {
          await sessionsApi.endSession(sessionId)
        } catch {
          // best-effort — the run already produced its measurements
        }
      }
      sessionStore.sessionEnded()
    },
    config: {
      durationMs: Math.max(schedule.totalDurationMs + DRAIN_MARGIN_MS, TARGET_DURATION_MS),
      driftThresholdMsPerTurn: DRIFT_THRESHOLD_MS_PER_TURN,
      leakWarmupCount: LEAK_WARMUP_SAMPLES,
      leakThresholdBytesPerSample: LEAK_THRESHOLD_BYTES_PER_SAMPLE,
    },
  })

  void context.close()
  return report
}
