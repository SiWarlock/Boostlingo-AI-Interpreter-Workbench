// WER-via-script aggregation (ARCH-015 / §10 / §12). Per turn the harness scores the pipeline's REAL STT
// output against the known scripted reference; this aggregates to a per-mode headline. WER is UNBOUNDED —
// a hypothesis longer than the reference can exceed 1.0 — and is NEVER clamped (clamping would silently
// flatter a bad transcription). A turn with no reference text (can't be scored) or a non-finite WER is
// SKIPPED, never counted as a synthetic 0 (§21 skip-non-finite / §12/§25 degrade-to-null-never-fabricate).
// The per-turn `/wer` call is runtime/088; only the aggregation math is here.

export type SoakWerTurn = {
  referenceText: string
  werValue: number
}

export type SoakWerSummary = {
  meanWer: number | null
  medianWer: number | null
  count: number
}

export function aggregateWer(perTurn: SoakWerTurn[]): SoakWerSummary {
  const values: number[] = []
  for (const turn of perTurn) {
    if (turn.referenceText.trim() === '') {
      continue
    }
    if (typeof turn.werValue !== 'number' || !Number.isFinite(turn.werValue)) {
      continue
    }
    values.push(turn.werValue) // unbounded — kept as-is, never clamped to 1.0.
  }

  const count = values.length
  if (count === 0) {
    // No scorable turns → honest null headline (a synthetic 0 would read as a perfect score).
    return { meanWer: null, medianWer: null, count: 0 }
  }

  const meanWer = values.reduce((sum, v) => sum + v, 0) / count
  const sorted = [...values].sort((a, b) => a - b)
  const mid = Math.floor(count / 2)
  const medianWer = count % 2 === 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid]

  return { meanWer, medianWer, count }
}
