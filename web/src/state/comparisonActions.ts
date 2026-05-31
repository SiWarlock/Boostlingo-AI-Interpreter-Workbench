import { ApiError } from '../api/http'
import type { InterpretationSession, SessionSummary, UiError } from '../types/domain'
import { aggregateCostByVariant, toComparisonTurn } from './comparisonAggregation'
import type { VariantCost } from './comparisonAggregation'
import type { SessionStore } from './sessionStore'

// The Flow-E comparison data load (ARCH-009 / ARCH-017). Two sources, each authoritative for its slice
// (no double-truth): GET /summary owns the per-mode aggregates + WER (the headline); GET /session owns
// the canonical per-turn cost the by-variant split is derived from. DI'd + unit-tested (lesson §7);
// the component is a thin render-from-data caller. Errors → the store (single sink, lesson §2).

// The minimal api surface the flow needs (structurally satisfied by sessionsApi).
export type ComparisonApi = {
  getSummary(sessionId: string): Promise<SessionSummary>
  getSession(sessionId: string): Promise<InterpretationSession>
}

export type ComparisonDeps = {
  store: Pick<SessionStore, 'getState' | 'addError'>
  api: ComparisonApi
}

// byVariant === null signals the per-variant source (GET /session) failed/unavailable; [] means it ran
// but no turn was priced. The two are rendered distinctly (degraded vs empty). `models` attributes the
// model per mode (056 bug 6) from the session providerProfile — INDEPENDENT of cost (which may be absent,
// bug 5) so the comparison can name models regardless; null when GET /session failed (shares its source).
export type ComparisonData = {
  summary: SessionSummary
  byVariant: VariantCost[] | null
  models: { cascade: string; realtime: string } | null
}

function toUiError(error: unknown, fallback: UiError): UiError {
  return error instanceof ApiError ? error.uiError : fallback
}

export async function loadComparison(deps: ComparisonDeps): Promise<ComparisonData | null> {
  const { sessionId } = deps.store.getState()
  if (sessionId === null) {
    return null
  }

  // The headline (per-mode + WER). Without it there's no comparison → return null (no fabricated view).
  let summary: SessionSummary
  try {
    summary = await deps.api.getSummary(sessionId)
  } catch (error) {
    deps.store.addError(
      toUiError(error, {
        code: 'summary.load_failed',
        safeMessage: 'Could not load the session summary.',
        retryable: true,
      }),
    )
    return null
  }

  // The per-variant cost split + the per-mode model attribution, both from the canonical persisted session.
  // Degrades INDEPENDENTLY — a session fetch failure leaves byVariant + models null but the per-mode
  // comparison above still renders.
  let byVariant: VariantCost[] | null
  let models: ComparisonData['models']
  try {
    const session = await deps.api.getSession(sessionId)
    byVariant = aggregateCostByVariant(session.turns.map(toComparisonTurn))
    // Model identity per mode, independent of cost (bug 6): cascade = translation model, realtime = realtime model.
    models = {
      cascade: session.config.providerProfile.translationModel,
      realtime: session.config.providerProfile.realtimeModel,
    }
  } catch (error) {
    deps.store.addError(
      toUiError(error, {
        code: 'session.load_failed',
        safeMessage: 'Could not load the per-variant cost breakdown.',
        retryable: true,
      }),
    )
    byVariant = null
    models = null
  }

  return { summary, byVariant, models }
}
