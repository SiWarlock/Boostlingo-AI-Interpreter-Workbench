import { useEffect, useState } from 'react'
import { recordingController } from '../state/recordingActions'
import { realtimeTurnController } from '../realtime/realtimeTurnController'
import { canStartRecording, canStopRecording } from '../state/selectors'
import { useSessionState } from '../state/sessionStore'

// Start/Stop + capture timer + status (ARCH-007). Renders only from the store + selectors and delegates to
// the mode-appropriate turn orchestration — no transport/capture internals leak in (clean separation).
// Dispatches by currentMode: cascade -> recordingController (D.4b); realtime -> realtimeTurnController (E.4b,
// the realtime path's real entry point). Manual-smoke; the dispatch + transition tests are E.4b / D.7.
export default function RecordingControls() {
  const state = useSessionState()
  const [elapsedMs, setElapsedMs] = useState(0)

  useEffect(() => {
    if (state.turnStatus !== 'recording') {
      setElapsedMs(0)
      return
    }
    const startedAt = Date.now()
    const id = setInterval(() => setElapsedMs(Date.now() - startedAt), 200)
    return () => clearInterval(id)
  }, [state.turnStatus])

  const handleStart = (): void => {
    if (state.mode === 'realtime') {
      void realtimeTurnController.startTurn()
    } else {
      void recordingController.startRecording()
    }
  }

  const handleStop = (): void => {
    if (state.mode === 'realtime') {
      realtimeTurnController.stopTurn()
    } else {
      recordingController.stopRecording()
    }
  }

  return (
    <section aria-label="recording-controls">
      <button type="button" disabled={!canStartRecording(state)} onClick={handleStart}>
        Start recording
      </button>
      <button type="button" disabled={!canStopRecording(state)} onClick={handleStop}>
        Stop
      </button>
      <span aria-label="turn-status">Turn: {state.turnStatus}</span>
      {state.turnStatus === 'recording' && (
        <span aria-label="capture-timer"> · {(elapsedMs / 1000).toFixed(1)}s</span>
      )}
    </section>
  )
}
