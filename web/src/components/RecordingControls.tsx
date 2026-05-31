import { useEffect, useState } from 'react'
import { Headphones, Mic, Square } from 'lucide-react'
import { recordingController } from '../state/recordingActions'
import { realtimeTurnController } from '../realtime/realtimeTurnController'
import { canStartRecording, canStopRecording } from '../state/selectors'
import { useSessionState } from '../state/sessionStore'
import StatusPill from './StatusPill'

// Start/Stop + capture timer + status (ARCH-007). Renders only from the store + selectors and delegates to
// the mode-appropriate turn orchestration — no transport/capture internals leak in (clean separation).
// Dispatches by currentMode: cascade -> recordingController (D.4b); realtime -> realtimeTurnController (E.4b,
// the realtime path's real entry point). Manual-smoke; the dispatch + transition tests are E.4b / D.7.
//
// H.1 styling: the design's card + turn StatusPill + .rec-actions row. CSS/markup only — both buttons
// stay always-rendered with the SAME disabled gating; the turn-status + capture-timer aria-labels are
// preserved (turn-status now lives on the StatusPill).
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
    <section className="card card-pad" aria-label="recording-controls">
      <div className="card-hd">
        <span className="ic">
          <Mic size={18} aria-hidden />
        </span>
        <span className="card-title">Recording</span>
        <span className="right">
          <StatusPill value={state.turnStatus} ariaLabel="turn-status" />
        </span>
      </div>

      <div className="rec-actions">
        <button
          type="button"
          className="btn btn-primary"
          disabled={!canStartRecording(state)}
          onClick={handleStart}
        >
          <span className="ic">
            <Mic size={17} aria-hidden />
          </span>
          Start recording
        </button>
        <button
          type="button"
          className="btn btn-outline"
          disabled={!canStopRecording(state)}
          onClick={handleStop}
        >
          <span className="ic">
            <Square size={17} aria-hidden />
          </span>
          Stop
        </button>
      </div>

      {state.turnStatus === 'recording' && (
        <div className="rec-status">
          <span aria-label="capture-timer">· {(elapsedMs / 1000).toFixed(1)}s</span>
        </div>
      )}

      <div className="rec-hint">
        <Headphones size={14} aria-hidden /> Use a headset to avoid echo.
      </div>
    </section>
  )
}
