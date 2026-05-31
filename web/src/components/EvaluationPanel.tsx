import { useEffect, useRef, useState } from 'react'
import { Info, Mic, Target } from 'lucide-react'
import { evaluationApi } from '../api/evaluationApi'
import { sessionsApi } from '../api/sessionsApi'
import { audioCaptureController } from '../audio/audioCaptureController'
import { runEvaluation } from '../state/evaluationActions'
import type { EvaluationOutcome } from '../state/evaluationActions'
import { canStartRecording } from '../state/selectors'
import { sessionStore, useSessionState } from '../state/sessionStore'
import type { EvaluationPhrase } from '../types/domain'

// WER Evaluation panel (Flow D, ARCH-015 / ARCH-007). Standalone: pick a scripted phrase, read + record
// it, transcribe (STT-only), score + PERSIST the WER against a dedicated eval turn. Renders from local
// state + the store (errors only); dispatches the DI'd evaluationActions flow — no transport internals
// here (clean separation, forbidden-pattern #3). WER is STT-only: it measures recognition quality, not
// translation quality (the spec-required explanation copy, verbatim from ARCH-015).
//
// H.1 styling: card + .eval-row (phrase + reference on the left, the WER score on the right). CSS/markup
// only — the single phrase <select> (getByRole('combobox')), the reference-text / wer-result / hypothesis
// aria-labels, the S/I/D text, the "Record & evaluate" button, the session hint, and the verbatim WER
// explanation are all unchanged. The flow logic (phrase load, handleEvaluate, gates) is untouched.
const WER_EXPLANATION =
  'WER compares the recognized transcript to a known reference phrase. It is useful for STT quality, not a full measure of translation quality.'

export default function EvaluationPanel() {
  const state = useSessionState()
  const [phrases, setPhrases] = useState<EvaluationPhrase[]>([])
  const [selectedPhraseId, setSelectedPhraseId] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<EvaluationOutcome | null>(null)
  const [evaluating, setEvaluating] = useState(false)
  // Guards the async window the disabled button can't cover (a double-click before the re-render
  // commits) so a second dispatch can't create a duplicate eval turn — the §11 inFlight pattern.
  const inFlightRef = useRef(false)

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

  const selected = phrases.find((p) => p.phraseId === selectedPhraseId) ?? null
  // Eval needs a live session (for the turnId / persist path) + no turn in flight — reuse the existing
  // recording gate plus a sessionId guard (read-only selector import; clean separation, ARCH-007).
  const canEvaluate =
    state.sessionId !== null && canStartRecording(state) && selected !== null && !evaluating

  const handleEvaluate = async (): Promise<void> => {
    if (selected === null || inFlightRef.current) return
    inFlightRef.current = true
    setEvaluating(true)
    try {
      const result = await runEvaluation(
        {
          store: sessionStore,
          api: evaluationApi,
          createTurn: (id) => sessionsApi.createTurn(id),
          capture: audioCaptureController,
        },
        { phraseId: selected.phraseId, language: selected.language },
      )
      if (result) setOutcome(result)
    } finally {
      inFlightRef.current = false
      setEvaluating(false)
    }
  }

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
          <button
            type="button"
            className="btn btn-primary"
            disabled={!canEvaluate}
            onClick={() => void handleEvaluate()}
          >
            {evaluating ? (
              <>
                <span className="spin" /> Evaluating…
              </>
            ) : (
              <>
                <span className="ic">
                  <Mic size={17} aria-hidden />
                </span>
                Record &amp; evaluate
              </>
            )}
          </button>
          {state.sessionId === null && (
            <p className="bl-sm" style={{ margin: 0 }}>
              Start a session to evaluate.
            </p>
          )}

          {outcome && (
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
