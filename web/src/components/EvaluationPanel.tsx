import { useEffect, useRef, useState } from 'react'
import { Info, Mic, Square, Target } from 'lucide-react'
import { evaluationApi } from '../api/evaluationApi'
import { sessionsApi } from '../api/sessionsApi'
import { audioCaptureController } from '../audio/audioCaptureController'
import type { BlobRecordingHandle } from '../audio/audioCaptureController'
import { evaluateFromBlob } from '../state/evaluationActions'
import type { EvaluationOutcome } from '../state/evaluationActions'
import { canStartRecording } from '../state/selectors'
import { sessionStore, useSessionState } from '../state/sessionStore'
import type { EvaluationPhrase } from '../types/domain'

// WER Evaluation panel (Flow D, ARCH-015 / ARCH-007). Standalone: pick a scripted phrase, read + record
// it (PUSH-TO-TALK — 096), transcribe (STT-only), score + PERSIST the WER against a dedicated eval turn.
// Renders from local state + the store (errors only); dispatches the DI'd evaluateFromBlob flow — no
// transport internals here (clean separation, forbidden-pattern #3). WER is STT-only: it measures
// recognition quality, not translation quality (the spec-required explanation copy, verbatim from ARCH-015).
//
// Push-to-talk state machine (Finding 3 fix): idle → countdown ("Get ready… 3·2·1") → recording (visible,
// user-controlled) → evaluating → result. The window is user-controlled (click Stop); a silent fixed
// auto-window produced mathematically-correct-but-misleading scores (the user spoke late / not at all). A
// no-speech capture degrades to an explicit "No speech detected — n/a", never a confident "100%".
const WER_EXPLANATION =
  'WER compares the recognized transcript to a known reference phrase. It is useful for STT quality, not a full measure of translation quality.'

// The "get ready" lead-in (seconds) before the mic opens — answers the live-repro complaint that the panel
// "never told me when to record". Recording then runs until the user clicks Stop.
const COUNTDOWN_START = 3

type EvalPhase = 'idle' | 'countdown' | 'recording' | 'evaluating' | 'result'

export default function EvaluationPanel() {
  const state = useSessionState()
  const [phrases, setPhrases] = useState<EvaluationPhrase[]>([])
  const [selectedPhraseId, setSelectedPhraseId] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<EvaluationOutcome | null>(null)
  const [phase, setPhase] = useState<EvalPhase>('idle')
  const [countdown, setCountdown] = useState(COUNTDOWN_START)
  // Guards the async window the disabled control can't cover (a double-click before the re-render commits)
  // so a second dispatch can't create a duplicate eval turn — the §11 inFlight pattern.
  const inFlightRef = useRef(false)
  const recordingHandleRef = useRef<BlobRecordingHandle | null>(null)
  const countdownTimerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  // Fetch the scripted phrases on mount (a sessionless GET). A failure routes to the store error sink —
  // never an unhandled rejection (mirrors the App config bootstrap).
  useEffect(() => {
    let cancelled = false
    evaluationApi
      .getPhrases()
      .then((loaded) => {
        if (cancelled) return
        setPhrases(loaded)
        if (loaded.length > 0) setSelectedPhraseId(loaded[0].phraseId)
      })
      .catch(() => {
        if (!cancelled)
          sessionStore.addError({
            code: 'evaluation.phrases_load_failed',
            safeMessage: 'Could not load the evaluation phrases.',
            retryable: true,
          })
      })
    return () => {
      cancelled = true
    }
  }, [])

  // On unmount, release any timer/recording so a navigated-away panel can't hold the mic open.
  useEffect(() => {
    return () => {
      if (countdownTimerRef.current !== null) clearInterval(countdownTimerRef.current)
      void recordingHandleRef.current?.stop()
    }
  }, [])

  const selected = phrases.find((p) => p.phraseId === selectedPhraseId) ?? null
  // Eval needs a live session (for the turnId / persist path) + no turn in flight — reuse the existing
  // recording gate plus a sessionId guard (read-only selector import; clean separation, ARCH-007).
  const canEvaluate = state.sessionId !== null && canStartRecording(state) && selected !== null

  function clearCountdown(): void {
    if (countdownTimerRef.current !== null) {
      clearInterval(countdownTimerRef.current)
      countdownTimerRef.current = null
    }
  }

  function resetToIdle(): void {
    clearCountdown()
    recordingHandleRef.current = null
    setPhase('idle')
  }

  // Record → a 3·2·1 "get ready" countdown, THEN open the mic (visible recording state until Stop).
  const handleRecord = (): void => {
    if (!canEvaluate || (phase !== 'idle' && phase !== 'result')) return
    setOutcome(null)
    setCountdown(COUNTDOWN_START)
    setPhase('countdown')
    let remaining = COUNTDOWN_START
    countdownTimerRef.current = setInterval(() => {
      remaining -= 1
      if (remaining <= 0) {
        clearCountdown()
        void beginRecording()
      } else {
        setCountdown(remaining)
      }
    }, 1000)
  }

  // Countdown done → open the mic. A null handle = mic denied / unsupported → sanitized capture.failed +
  // reset to idle (the capture controller returns null silently; the panel owns the user-facing error).
  const beginRecording = async (): Promise<void> => {
    const handle = await audioCaptureController.startBlobRecording()
    if (handle === null) {
      sessionStore.addError({
        code: 'capture.failed',
        safeMessage: 'Could not record audio. Check microphone access and retry.',
        retryable: true,
      })
      resetToIdle()
      return
    }
    recordingHandleRef.current = handle
    setPhase('recording')
  }

  // Stop → end the recording, then transcribe + score the captured blob (evaluateFromBlob owns the
  // no-speech degrade + the persist path). A capture failure resets to idle; a scored/no-speech result
  // renders (and Record is available again to retry).
  const handleStop = async (): Promise<void> => {
    const handle = recordingHandleRef.current
    if (handle === null || selected === null || inFlightRef.current) return
    inFlightRef.current = true
    setPhase('evaluating')
    try {
      const captured = await handle.stop()
      recordingHandleRef.current = null
      if (captured === null) {
        sessionStore.addError({
          code: 'capture.failed',
          safeMessage: 'Could not record audio. Check microphone access and retry.',
          retryable: true,
        })
        resetToIdle()
        return
      }
      const result = await evaluateFromBlob(
        {
          store: sessionStore,
          api: evaluationApi,
          createTurn: (id) => sessionsApi.createTurn(id),
        },
        captured.blob,
        { phraseId: selected.phraseId, language: selected.language },
      )
      if (result) {
        setOutcome(result)
        setPhase('result')
      } else {
        resetToIdle() // error already surfaced to the store
      }
    } finally {
      inFlightRef.current = false
    }
  }

  const showRecordButton = phase === 'idle' || phase === 'result'

  return (
    <section className="card card-pad" aria-label="evaluation-panel">
      <div className="card-hd">
        <span className="ic">
          <Target size={18} aria-hidden />
        </span>
        <span className="card-title">Evaluation · WER</span>
        <span className="right eyebrow">STT accuracy only</span>
      </div>

      <div className="eval-row">
        <div className="eval-ref">
          <label className="field" style={{ marginBottom: 0 }}>
            <span className="field-lab">Scripted phrase</span>
            <select
              className="select"
              value={selectedPhraseId ?? ''}
              onChange={(e) => setSelectedPhraseId(e.target.value)}
            >
              {phrases.map((p) => (
                <option key={p.phraseId} value={p.phraseId}>
                  {p.phraseId} ({p.language})
                </option>
              ))}
            </select>
          </label>
          {selected && (
            <p aria-label="reference-text" className="txt">
              {selected.referenceText}
            </p>
          )}
        </div>

        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 12,
            alignItems: 'center',
            minWidth: 180,
          }}
        >
          {showRecordButton && (
            <button
              type="button"
              className="btn btn-primary"
              disabled={!canEvaluate}
              onClick={() => handleRecord()}
            >
              <span className="ic">
                <Mic size={17} aria-hidden />
              </span>
              {phase === 'result' ? 'Record again' : 'Record'}
            </button>
          )}

          {phase === 'countdown' && (
            <p role="status" aria-live="polite" className="bl-sm" style={{ margin: 0 }}>
              Get ready… {countdown}
            </p>
          )}

          {phase === 'recording' && (
            <>
              <p role="status" aria-live="assertive" className="bl-sm" style={{ margin: 0 }}>
                Recording — read the phrase, then click Stop.
              </p>
              <button type="button" className="btn btn-primary" onClick={() => void handleStop()}>
                <span className="ic">
                  <Square size={16} aria-hidden />
                </span>
                Stop
              </button>
            </>
          )}

          {phase === 'evaluating' && (
            <p role="status" aria-live="polite" className="bl-sm" style={{ margin: 0 }}>
              <span className="spin" /> Evaluating…
            </p>
          )}

          {state.sessionId === null && (
            <p className="bl-sm" style={{ margin: 0 }}>
              Start a session to evaluate.
            </p>
          )}

          {phase === 'result' && outcome?.kind === 'no-speech' && (
            <div aria-label="wer-result" className="wer-score">
              <div className="metric-big" style={{ fontSize: 22 }}>
                No speech detected — n/a
              </div>
              <div className="eyebrow" style={{ marginTop: 2 }}>
                No audio was recognized — try again
              </div>
            </div>
          )}

          {phase === 'result' && outcome?.kind === 'scored' && (
            <div aria-label="wer-result" className="wer-score">
              <div className="metric-big" style={{ fontSize: 40 }}>
                {(outcome.werResult.wer * 100).toFixed(1)}%
              </div>
              <div className="eyebrow" style={{ marginTop: 2 }}>
                Word error rate
              </div>
              <div className="sid">
                <span className="s">Substitutions: {outcome.werResult.substitutions}</span>
                <span className="s">Insertions: {outcome.werResult.insertions}</span>
                <span className="s">Deletions: {outcome.werResult.deletions}</span>
              </div>
              <div className="sid">
                <span className="s">Reference words: {outcome.werResult.referenceWordCount}</span>
              </div>
              <p aria-label="hypothesis" className="bl-sm" style={{ marginTop: 8 }}>
                {outcome.hypothesis}
              </p>
            </div>
          )}
        </div>
      </div>

      <div className="rec-hint" style={{ marginTop: 14 }}>
        <Info size={13} aria-hidden /> {WER_EXPLANATION}
      </div>
    </section>
  )
}
