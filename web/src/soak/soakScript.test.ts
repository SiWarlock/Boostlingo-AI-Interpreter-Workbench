import { describe, expect, it } from 'vitest'
import { CANONICAL_SOAK_SCRIPT, validateSoakScript } from './soakScript'
import type { SoakScript } from './soakScript'

// The canonical scripted EN↔ES help-desk conversation is the KNOWN ground-truth for WER-via-script
// (G.4 design decision 3A): ~24 alternating utterances, both directions, ~5 min cadence. `validateSoakScript`
// pins the shape invariants so a future edit to the committed script can't silently break the soak harness
// (alternation = both detect-and-flip directions exercised; non-empty text = a real WER reference).
describe('validateSoakScript', () => {
  it('accepts the canonical script — alternates EN/ES, ~24 non-empty utterances, ~5min cadence', () => {
    const result = validateSoakScript(CANONICAL_SOAK_SCRIPT)
    expect(result.valid).toBe(true)
    expect(result.issues).toEqual([])
    // Decision 3A: ~24 utterances, both directions present (bidirectional detect+flip exercised both ways).
    expect(CANONICAL_SOAK_SCRIPT.utterances.length).toBeGreaterThanOrEqual(20)
    expect(CANONICAL_SOAK_SCRIPT.utterances.length).toBeLessThanOrEqual(28)
    const langs = new Set(CANONICAL_SOAK_SCRIPT.utterances.map((u) => u.sourceLang))
    expect(langs).toEqual(new Set(['en', 'es']))
  })

  it('rejects a non-alternating / too-short / single-language script', () => {
    const bad: SoakScript = {
      id: 'bad',
      gapMs: 6000,
      utterances: [
        { id: 'b1', sourceLang: 'en', text: 'one' },
        { id: 'b2', sourceLang: 'en', text: 'two' },
        { id: 'b3', sourceLang: 'en', text: 'three' },
      ],
    }
    const result = validateSoakScript(bad)
    expect(result.valid).toBe(false)
    expect(result.issues.length).toBeGreaterThan(0)
  })

  it('rejects a script with an empty / whitespace utterance (the WER ground-truth must be real text)', () => {
    const withEmpty: SoakScript = {
      ...CANONICAL_SOAK_SCRIPT,
      utterances: CANONICAL_SOAK_SCRIPT.utterances.map((u, i) =>
        i === 0 ? { ...u, text: '   ' } : u,
      ),
    }
    const result = validateSoakScript(withEmpty)
    expect(result.valid).toBe(false)
    expect(result.issues.some((m) => /empty/i.test(m))).toBe(true)
  })
})
