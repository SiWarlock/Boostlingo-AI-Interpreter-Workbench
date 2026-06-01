import { describe, expect, it } from 'vitest'
import { aggregateWer } from './soakWer'

// WER-via-script aggregation (ARCH-015 / §10 / §12). Per turn the harness scores the pipeline's real STT
// output against the known scripted text; this aggregates to a per-mode headline (mean + median + count).
// WER is UNBOUNDED — a hypothesis longer than the reference can exceed 1.0 — and is NEVER clamped (clamping
// would silently flatter a bad transcription). Mirrors the §21 "skip non-finite, never synthesize" posture.
describe('aggregateWer', () => {
  it('aggregates mean + median + count and never clamps WER above 1.0', () => {
    const summary = aggregateWer([
      { referenceText: 'a', werValue: 0 },
      { referenceText: 'b', werValue: 0.5 },
      { referenceText: 'c', werValue: 1.5 }, // > 1.0 — kept, NOT clamped.
    ])
    expect(summary.count).toBe(3)
    expect(summary.meanWer).toBeCloseTo((0 + 0.5 + 1.5) / 3, 6) // 0.6667
    expect(summary.medianWer).toBeCloseTo(0.5, 6)
  })

  it('skips empty-reference + non-finite WER turns; empty input → null headline, count 0', () => {
    const summary = aggregateWer([
      { referenceText: '', werValue: 0 }, // no ground truth — can't score, skipped.
      { referenceText: 'x', werValue: Number.NaN }, // unscored — skipped (never poisons the mean).
      { referenceText: 'y', werValue: 0.2 },
    ])
    expect(summary.count).toBe(1)
    expect(summary.meanWer).toBeCloseTo(0.2, 6)
    expect(summary.medianWer).toBeCloseTo(0.2, 6)

    const empty = aggregateWer([])
    expect(empty.count).toBe(0)
    expect(empty.meanWer).toBeNull() // honest null, never a synthetic 0 (which reads as a perfect score).
    expect(empty.medianWer).toBeNull()
  })
})
