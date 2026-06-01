import { evaluationApi } from '../api/evaluationApi'

// The WER production wiring (089b / 090) — binds the soak's abstract `computeWer(reference, hypothesis)`
// seam to the canonical backend `POST /api/evaluation/wer` via the additive explicit-reference path
// (`{sessionId, reference, hypothesis}`, no phraseId/turnId → no attach, no IsEvaluation, so the soak turns
// stay in the comparison). NO client-side WER calc — the backend owns the algorithm. Reuses `evaluationApi`
// + the shared `http` boundary, so a non-OK response surfaces as a sanitized ApiError (web §3).

export function createSoakWer(
  sessionId: string,
): (reference: string, hypothesis: string) => Promise<number> {
  return async (reference, hypothesis) => {
    const response = await evaluationApi.computeWer({ sessionId, reference, hypothesis })
    return response.result.wer
  }
}
