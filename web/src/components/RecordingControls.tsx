import { useEffect, useState } from 'react'
import { recordingController } from '../state/recordingActions'
import { canStartRecording, canStopRecording } from '../state/selectors'
import { useSessionState } from '../state/sessionStore'

// Start/Stop + capture timer + status (ARCH-007). Renders only from the store + selectors and
// delegates to the recording orchestration — no transport/capture internals leak in (clean
// separation). Manual-smoke (ARCH-020); the transition component test is D.7.
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

  return (
    <section aria-label="recording-controls">
      <button
        type="button"
        disabled={!canStartRecording(state)}
        onClick={() => void recordingController.startRecording()}
      >
        Start recording
      </button>
      <button
        type="button"
        disabled={!canStopRecording(state)}
        onClick={() => recordingController.stopRecording()}
      >
        Stop
      </button>
      <span aria-label="turn-status">Turn: {state.turnStatus}</span>
      {state.turnStatus === 'recording' && (
        <span aria-label="capture-timer"> · {(elapsedMs / 1000).toFixed(1)}s</span>
      )}
    </section>
  )
}
