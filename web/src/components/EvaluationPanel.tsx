import { useEffect, useRef, useState } from 'react'
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
    <section aria-label="evaluation-panel">
      <h2>WER Evaluation</h2>

      <label>
        Phrase
        <select
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

      {selected && <p aria-label="reference-text">{selected.referenceText}</p>}

      <button type="button" disabled={!canEvaluate} onClick={() => void handleEvaluate()}>
        {evaluating ? 'Evaluating…' : 'Record & evaluate'}
      </button>
      {state.sessionId === null && <p>Start a session to evaluate.</p>}

      {outcome && (
        <div aria-label="wer-result">
          <p>{`WER: ${(outcome.werResult.wer * 100).toFixed(1)}%`}</p>
          <p>{`Substitutions: ${outcome.werResult.substitutions}`}</p>
          <p>{`Insertions: ${outcome.werResult.insertions}`}</p>
          <p>{`Deletions: ${outcome.werResult.deletions}`}</p>
          <p>{`Reference words: ${outcome.werResult.referenceWordCount}`}</p>
          <p aria-label="hypothesis">{outcome.hypothesis}</p>
        </div>
      )}

      <p>{WER_EXPLANATION}</p>
    </section>
  )
}
