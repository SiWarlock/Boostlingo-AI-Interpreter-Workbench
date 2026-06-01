import { ApiError } from '../api/http'
import { sessionsApi } from '../api/sessionsApi'
import { audioCaptureController } from '../audio/audioCaptureController'
import type { AudioCaptureController, StreamingHandle } from '../audio/audioCaptureController'
import { cascadeStreamClient } from '../cascade/cascadeStreamClient'
import type { CascadeStreamClient } from '../cascade/cascadeStreamClient'
import { sessionStore } from './sessionStore'
import type { SessionStore } from './sessionStore'
import type { LatencyEvent } from '../types/domain'

// The recording orchestration (ARCH-011): sequences the whole cascade turn. DI'd + unit-tested against
// mocks; the production singleton wires the real store/api/client/capture. The controller holds the
// capture handle between start and stop. The component stays a thin store/selectors projection.

export type RecordingDeps = {
  store: Pick<
    SessionStore,
    'getState' | 'beginTurn' | 'failTurn' | 'addError' | 'setTurnStatus' | 'appendLatencyEvent'
  >
  createTurn: (sessionId: string) => Promise<{ turnId: string }>
  client: Pick<CascadeStreamClient, 'start' | 'sendFrame' | 'stop'>
  capture: Pick<AudioCaptureController, 'startStreaming'>
}

export type RecordingController = {
  startRecording: () => Promise<void>
  stopRecording: () => void
  // The cascade auto-VAD capture-stop hook (I.3): the backend auto-finalizes the turn (no frontend Stop),
  // so the cascade client invokes this on the turn-end to stop the mic. Idempotent with stopRecording.
  onCascadeTerminal: () => void
}

// A browser-clock turn-lifecycle marker (relativeMs is a placeholder — the top-level latency deltas
// use absolute timestamps, never relativeMs; D.6). recording.started/stopped give the frontend the
// client timestamps deriveTurnMetrics needs (the backend can't supply them for cascade).
function recordingMarker(name: string): LatencyEvent {
  return {
    name,
    stage: 'overall',
    timestamp: new Date().toISOString(),
    relativeMs: 0,
    clockSource: 'browser',
    metadata: {},
  }
}

export function createRecordingController(deps: RecordingDeps): RecordingController {
  let captureHandle: StreamingHandle | null = null
  // Guards against a re-entrant start (e.g. a Start double-click) racing in the window before beginTurn
  // flips turnStatus to 'recording' — a second turn would otherwise orphan a server turn.
  let inFlight = false

  async function startRecording(): Promise<void> {
    if (inFlight) {
      return
    }
    const state = deps.store.getState()
    const { sessionId } = state
    if (sessionId === null) {
      return // no active session — Start is also gated by canStartRecording
    }
    inFlight = true
    try {
      let turnId: string
      try {
        const created = await deps.createTurn(sessionId)
        turnId = created.turnId
      } catch (error) {
        deps.store.addError(
          error instanceof ApiError
            ? error.uiError
            : {
                code: 'turn.create_failed',
                safeMessage: 'Could not start the turn.',
                retryable: true,
              },
        )
        return // abort BEFORE any capture/WS resource exists
      }

      deps.store.beginTurn({ turnId, mode: state.mode, direction: state.direction })

      // Start capture first to learn the actual sampleRate; frames begin flowing to sendFrame, which the
      // client queues until the socket opens (the start frame carries the rate; no resample).
      captureHandle = await deps.capture.startStreaming({
        onFrame: (frame) => deps.client.sendFrame(frame),
        onError: (uiError) => deps.store.failTurn(uiError),
      })
      if (captureHandle === null) {
        return // mic denied / capture failed — onError already surfaced; do not open the WS
      }

      // Capture is live → stamp recording.started (browser clock) as the totalTurn origin (D.6).
      deps.store.appendLatencyEvent(recordingMarker('turn.recording.started'))

      deps.client.start({
        sessionId,
        turnId,
        direction: state.direction,
        sampleRate: captureHandle.sampleRate,
        translationModel: state.translationModel,
        ttsVoice: '', // blank -> the backend ResolveVoice picks the per-target-language voice
        // Phase-I (I.3): only in auto mode → the backend auto-finalizes on Deepgram utterance-end. Omitted
        // in manual (the key is absent, not present-false) so the manual frame is byte-identical to pre-062.
        ...(state.turnControlMode === 'auto' ? { autoVad: true } : {}),
        // Phase J (J.3): only when bidirectional → the backend auto-detects the source language per utterance
        // + flips direction. Omitted otherwise so the one-direction start frame stays byte-identical (078).
        ...(state.bidirectional ? { bidirectional: true } : {}),
      })
    } finally {
      inFlight = false
    }
  }

  // The cascade auto-VAD turn-end hook (I.3): the backend auto-finalizes the turn (the `done` frame already
  // ran completeTurn) without a frontend Stop, so the mic would keep running — stop + clear the capture
  // here. Idempotent: the optional-chain + null-out makes a 2nd call (or a prior manual stopRecording, which
  // already nulled the handle) a no-op. Stamps NO recording.stopped (that would be turn-complete time, not
  // speech-end → a wrong delta; cascade responsiveness anchors on stt.final, web §25) + no setTurnStatus
  // (completeTurn already moved the turn + set the status). Wired at the composition root (main.tsx).
  function onCascadeTerminal(): void {
    captureHandle?.stop()
    captureHandle = null
  }

  function stopRecording(): void {
    captureHandle?.stop()
    captureHandle = null
    // Stamp recording.stopped — the speechEnd marker every speechEnd->* delta measures from (browser
    // clock, D.6). The store no-ops if there's no current turn, so this is safe without a prior start.
    deps.store.appendLatencyEvent(recordingMarker('turn.recording.stopped'))
    deps.client.stop()
    deps.store.setTurnStatus('processing') // awaiting trailing finals + the `done` -> completeTurn
  }

  return { startRecording, stopRecording, onCascadeTerminal }
}

// Production singleton — wires the real collaborators (onAudio playback is wired at the
// cascadeStreamClient construction site; D.5).
export const recordingController = createRecordingController({
  store: sessionStore,
  createTurn: (sessionId) => sessionsApi.createTurn(sessionId),
  client: cascadeStreamClient,
  capture: audioCaptureController,
})
