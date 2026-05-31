import type {
  CostEstimate,
  InterpretationMode,
  InterpretationTurn,
  LatencyEvent,
  TranscriptSegment,
  TurnStatus,
  WerResult,
} from '../types/domain'

// 071 H.3-frontend drill-in: a FOCUSED projection (like F.3's ComparisonTurn, web §21) over the OPAQUE wire
// InterpretationTurn (Record<string, unknown> from GET /api/sessions/{id}). It reads ONLY the fields the
// per-turn breakdown renders — keeping the opaque-until-consumed posture (the deferred-opaque row in
// web/CLAUDE.md), no graduation of the wire turn. ⭐ The wire cost field is `costEstimate`, NOT the viewmodel
// `cost` (web §21 — reading `cost` silently empties it); map it. `latencyEvents`/`transcripts` pass through
// RAW (deriveTurnMetrics §25 consumes the events; the breakdown renders the segments). Defensive over the
// opaque shape: a non-array → [], an absent field → null/false (never throws on a sparse/failed persisted turn).

export type TurnDetailView = {
  turnId: string
  mode: InterpretationMode
  status: TurnStatus
  latencyEvents: LatencyEvent[]
  transcripts: TranscriptSegment[]
  cost: CostEstimate | null
  translationModelUsed: string | null
  werResult: WerResult | null
  isEvaluation: boolean
}

function asArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? (value as T[]) : []
}

export function toTurnDetailView(turn: InterpretationTurn): TurnDetailView {
  const t = turn as Record<string, unknown>
  return {
    turnId: typeof t.turnId === 'string' ? t.turnId : '',
    mode: t.mode as InterpretationMode,
    status: t.status as TurnStatus,
    latencyEvents: asArray<LatencyEvent>(t.latencyEvents),
    transcripts: asArray<TranscriptSegment>(t.transcripts),
    // ⭐ wire `costEstimate`, not the viewmodel `cost` (web §21); a real null/absent → null.
    cost: t.costEstimate ? (t.costEstimate as CostEstimate) : null,
    translationModelUsed:
      typeof t.translationModelUsed === 'string' ? t.translationModelUsed : null,
    werResult: t.werResult ? (t.werResult as WerResult) : null,
    isEvaluation: t.isEvaluation === true,
  }
}
