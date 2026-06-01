import { ApiError } from '../api/http'
import type { SessionStore } from './sessionStore'
import type {
  LanguageCode,
  TranscribeParams,
  TranscribeResponse,
  UiError,
  WerRequest,
  WerResponse,
  WerResult,
} from '../types/domain'

// The standalone WER evaluate-from-blob flow (Flow D, ARCH-015 / ARCH-017). DI'd + unit-tested against the
// real store + mocked api (lesson §7). The panel owns the push-to-talk recording lifecycle (096:
// startBlobRecording → user Stop → blob) and hands the captured blob here. Sequence:
//   zero-byte guard -> transcribe (STT-only) -> NO-SPEECH guard -> create a DEDICATED eval turn ->
//   compute + PERSIST the WER against that turn (so F.3's WerSummary can aggregate it).
// Errors route to the store (single sink, lesson §2); the OUTCOME is RETURNED for the panel's local
// display state (transient, not session state).
//
// The eval turn is a backend-only artifact (the WerResult attach point) — the store's interpretation
// turn machine (currentTurn / turns[]) is intentionally untouched here (no beginTurn / completeTurn).

// The minimal api surface the flow needs (structurally satisfied by evaluationApi).
export type EvaluationApi = {
  transcribe(params: TranscribeParams, audio: Blob): Promise<TranscribeResponse>
  computeWer(req: WerRequest): Promise<WerResponse>
}

export type EvaluationDeps = {
  store: Pick<SessionStore, 'getState' | 'addError'>
  api: EvaluationApi
  createTurn: (sessionId: string) => Promise<{ turnId: string }>
}

// A discriminated outcome (096): a real WER score, OR a distinct no-speech result when the STT heard
// nothing. The panel renders these differently — a no-speech capture must NEVER show a confident score
// (an empty hypothesis scored against a reference = all-deletions = a misleading "100%"). Finding 3.
export type EvaluationOutcome =
  | { kind: 'scored'; hypothesis: string; werResult: WerResult }
  | { kind: 'no-speech' }

// An ApiError already carries a sanitized UiError; anything else gets the fixed fallback (no raw leak).
function toUiError(error: unknown, fallback: UiError): UiError {
  return error instanceof ApiError ? error.uiError : fallback
}

export async function evaluateFromBlob(
  deps: EvaluationDeps,
  blob: Blob,
  input: { phraseId: string; language: LanguageCode },
): Promise<EvaluationOutcome | null> {
  const { sessionId } = deps.store.getState()
  if (sessionId === null) {
    return null // the UI gates on an active session; guard the flow too.
  }

  // 1. Guard a ZERO-BYTE blob → don't POST empty audio to the paid /transcribe STT endpoint (a wasted
  //    round-trip + a confusing downstream result). Surface a distinct, actionable "nothing was recorded"
  //    error and abort before transcribe (060 hardening; extends §20). The mic-denied path is handled by
  //    the panel (startBlobRecording → null → capture.failed); this flow only sees a captured blob.
  if (blob.size === 0) {
    deps.store.addError({
      code: 'capture.empty',
      safeMessage: 'No audio was captured. Check your microphone and try again.',
      retryable: true,
    })
    return null
  }

  // 2. Transcribe (STT-only). On failure, abort before creating a turn or scoring.
  let transcribed: TranscribeResponse
  try {
    transcribed = await deps.api.transcribe(
      { sessionId, phraseId: input.phraseId, language: input.language },
      blob,
    )
  } catch (error) {
    deps.store.addError(
      toUiError(error, {
        code: 'evaluation.transcribe_failed',
        safeMessage: 'Could not transcribe the recording.',
        retryable: true,
      }),
    )
    return null
  }

  // 2.5 NO-SPEECH guard (096 / Finding 3): the STT is the judge — an empty/whitespace hypothesis means it
  //     heard nothing (the user spoke late / not at all). Return a DISTINCT no-speech outcome — do NOT
  //     create an eval turn or compute a WER. Scoring an empty hypothesis against a reference yields
  //     all-deletions = a misleading "100%"; degrade honestly to n/a (never a confident score). Not an
  //     error (a valid result the panel renders); creating no turn also avoids a no-WerResult orphan.
  if (transcribed.hypothesis.trim() === '') {
    return { kind: 'no-speech' }
  }

  // 3. Create a dedicated eval turn (Q1=a) to attach the WerResult to. A failure aborts before scoring
  //    — don't compute/persist against a turn that never got created.
  let turnId: string
  try {
    const created = await deps.createTurn(sessionId)
    turnId = created.turnId
  } catch (error) {
    deps.store.addError(
      toUiError(error, {
        code: 'turn.create_failed',
        safeMessage: 'Could not create an evaluation turn.',
        retryable: true,
      }),
    )
    return null
  }

  // 4. Compute + PERSIST the WER against the turn (turnId -> the backend attaches it + writes the
  //    session JSON for F.3's WerSummary).
  let werResponse: WerResponse
  try {
    werResponse = await deps.api.computeWer({
      sessionId,
      turnId,
      phraseId: input.phraseId,
      hypothesis: transcribed.hypothesis,
    })
    // KNOWN LIMITATION (Q1=a): a computeWer failure here leaves the step-3 eval turn orphaned (a valid
    // turn with no WerResult attached). Bounded for the single-trusted-user demo; a turn cancel/cleanup
    // endpoint is a later (F.3 / G) concern — flagged at Step 9.
  } catch (error) {
    deps.store.addError(
      toUiError(error, {
        code: 'evaluation.wer_failed',
        safeMessage: 'Could not compute the WER score.',
        retryable: true,
      }),
    )
    return null
  }

  // 5. Surface an optional persistence warning (200-with-warning; degrade, don't crash — mirrors
  //    endSession). The score still computed; only the turn-attach write degraded.
  if (werResponse.persistenceWarning) {
    deps.store.addError(werResponse.persistenceWarning)
  }

  return { kind: 'scored', hypothesis: transcribed.hypothesis, werResult: werResponse.result }
}
