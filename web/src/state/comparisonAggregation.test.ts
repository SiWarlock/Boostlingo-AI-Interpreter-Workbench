import { describe, expect, it } from 'vitest'
import { aggregateCostByVariant, toComparisonTurn } from './comparisonAggregation'
import type {
  ComparisonTurn,
  CostEstimate,
  InterpretationMode,
  InterpretationTurn,
} from '../types/domain'

// Build a ComparisonTurn with (or without) a priced cost. `perMin === null` keeps the CostEstimate but
// nulls estimatedUsdPerMinute (the unpriced case); `model === null` drops the cost entirely.
function turn(
  mode: InterpretationMode,
  model: string | null,
  perMin: number | null,
): ComparisonTurn {
  if (model === null) {
    return { mode, cost: null }
  }
  const cost: CostEstimate = {
    provider: 'openai',
    model,
    pricingBasis: 'tokens',
    estimatedUsd: 0,
    estimatedUsdPerMinute: perMin,
    units: {},
    pricingConfigVersion: 'v',
    assumptions: [],
  }
  return { mode, cost }
}

describe('aggregateCostByVariant', () => {
  it('groups cost by (mode, model) — one row per distinct variant present', () => {
    const rows = aggregateCostByVariant([
      turn('cascade', 'gpt-5.4-nano', 0.1),
      turn('cascade', 'gpt-5.4-mini', 0.3),
      turn('realtime', 'gpt-realtime', 0.5),
    ])

    expect(rows).toHaveLength(3)
    expect(rows).toContainEqual({
      mode: 'cascade',
      model: 'gpt-5.4-nano',
      avgCostPerMinuteUsd: 0.1,
      turnCount: 1,
    })
    expect(rows).toContainEqual({
      mode: 'cascade',
      model: 'gpt-5.4-mini',
      avgCostPerMinuteUsd: 0.3,
      turnCount: 1,
    })
    expect(rows).toContainEqual({
      mode: 'realtime',
      model: 'gpt-realtime',
      avgCostPerMinuteUsd: 0.5,
      turnCount: 1,
    })
  })

  it('averages cost/min per variant across its turns', () => {
    const rows = aggregateCostByVariant([
      turn('cascade', 'gpt-5.4-nano', 0.1),
      turn('cascade', 'gpt-5.4-nano', 0.2),
    ])

    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ mode: 'cascade', model: 'gpt-5.4-nano', turnCount: 2 })
    expect(rows[0].avgCostPerMinuteUsd).toBeCloseTo(0.15, 10)
  })

  it('skips null/unpriced cost — never fabricates a synthetic 0', () => {
    const rows = aggregateCostByVariant([
      turn('cascade', 'gpt-5.4-nano', 0.2),
      turn('cascade', null, null), // cost entirely absent
      turn('realtime', 'gpt-realtime', null), // cost present but estimatedUsdPerMinute null
      turn('cascade', 'gpt-5.4-mini', Number.NaN), // non-finite per-minute (malformed payload)
    ])

    // The priced nano turn keeps avg 0.20 — NOT dragged toward 0 by the null-cost turn.
    expect(rows).toContainEqual({
      mode: 'cascade',
      model: 'gpt-5.4-nano',
      avgCostPerMinuteUsd: 0.2,
      turnCount: 1,
    })
    // The realtime variant with a null per-minute is ABSENT (not a fabricated 0 row).
    expect(rows.find((r) => r.mode === 'realtime')).toBeUndefined()
    // A non-finite (NaN) per-minute is skipped — never a "$NaN/min" row.
    expect(rows.find((r) => r.model === 'gpt-5.4-mini')).toBeUndefined()
  })

  it('returns an empty breakdown for an empty session', () => {
    expect(aggregateCostByVariant([])).toEqual([])
  })

  it('distinguishes the two realtime model variants', () => {
    const rows = aggregateCostByVariant([
      turn('realtime', 'gpt-realtime', 0.4),
      turn('realtime', 'gpt-realtime-mini', 0.2),
    ])

    expect(rows).toHaveLength(2)
    expect(rows).toContainEqual({
      mode: 'realtime',
      model: 'gpt-realtime',
      avgCostPerMinuteUsd: 0.4,
      turnCount: 1,
    })
    expect(rows).toContainEqual({
      mode: 'realtime',
      model: 'gpt-realtime-mini',
      avgCostPerMinuteUsd: 0.2,
      turnCount: 1,
    })
  })
})

describe('toComparisonTurn', () => {
  it('reads the wire `costEstimate` field (NOT the viewmodel `cost`) and defaults absent cost to null', () => {
    const wireCost: CostEstimate = {
      provider: 'openai',
      model: 'gpt-5.4-nano',
      pricingBasis: 'tokens',
      estimatedUsd: 0,
      estimatedUsdPerMinute: 0.3,
      units: {},
      pricingConfigVersion: 'v',
      assumptions: [],
    }

    // A persisted wire turn carries `costEstimate` (C# InterpretationTurn.CostEstimate).
    const withCost = toComparisonTurn({
      turnId: 't1',
      mode: 'cascade',
      costEstimate: wireCost,
    } as unknown as InterpretationTurn)
    expect(withCost).toEqual({ mode: 'cascade', cost: wireCost })

    // Absent cost → null (the unpriced/degraded turn).
    const withoutCost = toComparisonTurn({
      turnId: 't2',
      mode: 'realtime',
    } as unknown as InterpretationTurn)
    expect(withoutCost).toEqual({ mode: 'realtime', cost: null })

    // GUARD: reading the WRONG field (the viewmodel `cost`) would wrongly pick this up → must stay null.
    const onlyViewmodelCost = toComparisonTurn({
      mode: 'cascade',
      cost: wireCost,
    } as unknown as InterpretationTurn)
    expect(onlyViewmodelCost.cost).toBeNull()

    // A missing/unrecognized mode defaults to 'cascade' (a defensive default; assert it so a future
    // mode-enum expansion is noticed rather than silently mapped to cascade).
    const garbageMode = toComparisonTurn({ turnId: 't3' } as unknown as InterpretationTurn)
    expect(garbageMode.mode).toBe('cascade')
  })
})
