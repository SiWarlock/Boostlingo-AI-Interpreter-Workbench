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
  // flips turnStatus to 'recording' — a second turn would otherwise orphan a server turn. Also held across
  // the async J.5 re-arm so a concurrent Start can't acquire a 2nd mic over the still-live one.
  let inFlight = false
  // J.5 continuous-listening: set true by the user's end-conversation Stop (auto mode) — breaks the re-arm
  // loop so the next auto-finalized `done` cleans up instead of beginning another turn. Reset on each Start.
  let userEnded = false

  const sessionLive = (state: { sessionId: string | null; sessionStatus: string }): boolean =>
    state.sessionId !== null &&
    (state.sessionStatus === 'active' || state.sessionStatus === 'readyForTurn')

  // Stamp the browser-clock recording.started origin (D.6) + open a fresh WS for `turnId` over the (already
  // live) capture. Shared by the first turn (startRecording) and each continuous re-arm (J.5) so the start
  // frame is byte-identical: autoVad only in auto (I.3), bidirectional only when on (J.3).
  function startTurnFrame(sessionId: string, turnId: string, sampleRate: number): void {
    const state = deps.store.getState()
    deps.store.appendLatencyEvent(recordingMarker('turn.recording.started'))
    deps.client.start({
      sessionId,
      turnId,
      direction: state.direction,
      sampleRate,
      translationModel: state.translationModel,
      ttsVoice: '', // blank -> the backend ResolveVoice picks the per-target-language voice
      ...(state.turnControlMode === 'auto' ? { autoVad: true } : {}),
      ...(state.bidirectional ? { bidirectional: true } : {}),
    })
  }

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
    userEnded = false // a fresh Start begins a new conversation — clear any prior end flag
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
      // client queues until the socket opens (the start frame carries the rate; no resample). The mic stream
      // then stays alive across continuous turns (J.5) — each re-arm opens only a new WS, no re-acquire.
      captureHandle = await deps.capture.startStreaming({
        onFrame: (frame) => deps.client.sendFrame(frame),
        onError: (uiError) => deps.store.failTurn(uiError),
      })
      if (captureHandle === null) {
        return // mic denied / capture failed — onError already surfaced; do not open the WS
      }

      startTurnFrame(sessionId, turnId, captureHandle.sampleRate)
    } finally {
      inFlight = false
    }
  }

  // The cascade auto-VAD turn-end hook (I.3 → J.5). The backend auto-finalizes the turn (the `done` frame
  // already ran completeTurn) with no frontend Stop. In auto mode, on a COMPLETED terminal of a live session
  // the user hasn't ended, AUTO-BEGIN the next turn (a new WS over the still-live mic) — the hands-free
  // continuous loop. Otherwise (manual / a failed terminal / not-live / user-ended) take the END path: stop
  // + clear the mic (idempotent — optional-chain + null-out). The store's turnStatus, set by
  // completeTurn/failTurn BEFORE this hook fires, is the completed-vs-failed outcome seam (no new signal).
  function onCascadeTerminal(): void {
    const state = deps.store.getState()
    const handle = captureHandle // snapshot (a const TS can narrow; captureHandle is closure-reassigned)
    const rearmable =
      handle !== null &&
      state.turnStatus === 'completed' &&
      state.turnControlMode === 'auto' &&
      sessionLive(state) &&
      !userEnded
    if (rearmable && handle) {
      // Hold 'recording' SYNCHRONOUSLY so the Start button can't re-enable in the async re-arm gap (a
      // concurrent Start would startStreaming over the live mic → null handle → orphaned mic). Mic stays alive.
      deps.store.setTurnStatus('recording')
      void rearmCascadeTurn(state.sessionId as string, handle.sampleRate)
      return
    }
    captureHandle?.stop()
    captureHandle = null
  }

  // Begin the next continuous turn (J.5): a fresh turnId + a new WS over the already-live mic. inFlight-guarded
  // (a concurrent Start is dropped). A createTurn failure DEGRADES GRACEFULLY — surface the error, stop the
  // mic (don't spin/orphan), and UNSTICK the synchronous 'recording' to a recoverable 'failed' (the user can
  // Start a new conversation). A user-end / session-end that lands during the async createTurn aborts cleanly.
  async function rearmCascadeTurn(sessionId: string, sampleRate: number): Promise<void> {
    if (inFlight) {
      return
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
        captureHandle?.stop()
        captureHandle = null
        deps.store.setTurnStatus('failed') // unstick the synchronous 'recording' → recoverable
        return
      }
      // A user-end / session-end (or lost mic) during the async createTurn → drop the re-armed turn cleanly.
      const state = deps.store.getState()
      if (userEnded || !sessionLive(state) || captureHandle === null) {
        captureHandle?.stop()
        captureHandle = null
        return
      }
      deps.store.beginTurn({ turnId, mode: state.mode, direction: state.direction })
      startTurnFrame(sessionId, turnId, sampleRate)
    } finally {
      inFlight = false
    }
  }

  function stopRecording(): void {
    // J.5: in auto (continuous) mode the Stop button is the "end conversation" control — set the user-end
    // flag so the in-flight turn's `done` won't re-arm, finalize that turn (client.stop, idempotent with the
    // §34 utterance-end terminal), and stop the mic. NO recording.stopped marker (the auto speech-end anchor
    // is backend-stamped; web §25). Manual mode is unchanged (single-turn stop).
    if (deps.store.getState().turnControlMode === 'auto') {
      userEnded = true
      deps.client.stop()
      captureHandle?.stop()
      captureHandle = null
      deps.store.setTurnStatus('processing')
      return
    }
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
