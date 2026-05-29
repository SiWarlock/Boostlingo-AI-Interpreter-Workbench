import { ApiError } from '../api/http'
import { sessionsApi } from '../api/sessionsApi'
import { audioCaptureController } from '../audio/audioCaptureController'
import type { AudioCaptureController, StreamingHandle } from '../audio/audioCaptureController'
import { cascadeStreamClient } from '../cascade/cascadeStreamClient'
import type { CascadeStreamClient } from '../cascade/cascadeStreamClient'
import { sessionStore } from './sessionStore'
import type { SessionStore } from './sessionStore'

// The recording orchestration (ARCH-011): sequences the whole cascade turn. DI'd + unit-tested against
// mocks; the production singleton wires the real store/api/client/capture. The controller holds the
// capture handle between start and stop. The component stays a thin store/selectors projection.

export type RecordingDeps = {
  store: Pick<SessionStore, 'getState' | 'beginTurn' | 'failTurn' | 'addError' | 'setTurnStatus'>
  createTurn: (sessionId: string) => Promise<{ turnId: string }>
  client: Pick<CascadeStreamClient, 'start' | 'sendFrame' | 'stop'>
  capture: Pick<AudioCaptureController, 'startStreaming'>
}

export type RecordingController = {
  startRecording: () => Promise<void>
  stopRecording: () => void
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

      deps.client.start({
        sessionId,
        turnId,
        direction: state.direction,
        sampleRate: captureHandle.sampleRate,
        translationModel: state.translationModel,
        ttsVoice: '', // blank -> the backend ResolveVoice picks the per-target-language voice
      })
    } finally {
      inFlight = false
    }
  }

  function stopRecording(): void {
    captureHandle?.stop()
    captureHandle = null
    deps.client.stop()
    deps.store.setTurnStatus('processing') // awaiting trailing finals + the `done` -> completeTurn
  }

  return { startRecording, stopRecording }
}

// Production singleton — wires the real collaborators (onAudio playback is wired at the
// cascadeStreamClient construction site; D.5).
export const recordingController = createRecordingController({
  store: sessionStore,
  createTurn: (sessionId) => sessionsApi.createTurn(sessionId),
  client: cascadeStreamClient,
  capture: audioCaptureController,
})
