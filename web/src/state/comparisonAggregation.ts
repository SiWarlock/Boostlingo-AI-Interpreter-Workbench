import type {
  ComparisonTurn,
  CostEstimate,
  InterpretationMode,
  InterpretationTurn,
} from '../types/domain'

// Pure per-model-variant cost aggregation for the F.3 comparison (Flow E, ARCH-014 / ARCH-009). The
// §13 precedent: the backend prices each turn but does NOT pre-aggregate cost by model, so the frontend
// derives the by-variant split from the session's persisted per-turn cost. Each turn's CostEstimate.model
// IS the variant used (cascade = translation model; realtime = realtime model, set by E.2b).

export type VariantCost = {
  mode: InterpretationMode
  model: string
  avgCostPerMinuteUsd: number
  turnCount: number
}

function isCostEstimate(value: unknown): value is CostEstimate {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as Record<string, unknown>).model === 'string'
  )
}

// Project the opaque wire turn to the minimal {mode, cost} the comparison needs. Reads the wire
// `costEstimate` field (C# InterpretationTurn.CostEstimate) — NOT the frontend TurnViewModel.cost.
// Realtime turns carry it (priced at /complete, E.2b); cascade turns carry it (WS-priced); null when
// degraded (then skipped by the aggregation).
export function toComparisonTurn(turn: InterpretationTurn): ComparisonTurn {
  const mode: InterpretationMode = turn.mode === 'realtime' ? 'realtime' : 'cascade'
  const cost = isCostEstimate(turn.costEstimate) ? turn.costEstimate : null
  return { mode, cost }
}

// Group by (mode, model) and average estimatedUsdPerMinute. A turn with null cost OR a null/absent
// estimatedUsdPerMinute is SKIPPED — never counted as a synthetic 0 (§9/§13; a fabricated 0 reads as
// "free"). A variant with no priced turns simply doesn't appear (no fake row). Empty input → [].
export function aggregateCostByVariant(turns: ComparisonTurn[]): VariantCost[] {
  const groups = new Map<
    string,
    { mode: InterpretationMode; model: string; sum: number; count: number }
  >()

  for (const { mode, cost } of turns) {
    // Skip null cost AND any non-finite per-minute (null/undefined/NaN/Infinity/non-number) — never let
    // a synthetic 0 or a NaN/Infinity from a malformed payload corrupt the average; it degrades to "absent".
    if (
      cost === null ||
      typeof cost.estimatedUsdPerMinute !== 'number' ||
      !Number.isFinite(cost.estimatedUsdPerMinute)
    ) {
      continue
    }
    const key = `${mode}::${cost.model}`
    const group = groups.get(key)
    if (group) {
      group.sum += cost.estimatedUsdPerMinute
      group.count += 1
    } else {
      groups.set(key, { mode, model: cost.model, sum: cost.estimatedUsdPerMinute, count: 1 })
    }
  }

  return [...groups.values()].map((g) => ({
    mode: g.mode,
    model: g.model,
    avgCostPerMinuteUsd: g.sum / g.count,
    turnCount: g.count,
  }))
}
