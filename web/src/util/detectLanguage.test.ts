import { describe, expect, it } from 'vitest'
import { detectLanguage } from './detectLanguage'

// Deterministic EN/ES heuristic (Phase J / J.3) — a DISPLAY badge only (realtime emits no explicit
// language tag, so the realtime direction is best-effort; cascade rides the backend-resolved `direction`
// message, a measured Deepgram signal). Returns null on signal-less text so the CALLER falls back to the
// configured source — the "fall back to configured source on ambiguity" policy lives at the call site,
// not in this pure function.
describe('detectLanguage', () => {
  it('returns es for Spanish text (inverted punctuation / diacritics / stopword density)', () => {
    // Strong markers — inverted punctuation is unambiguously Spanish.
    expect(detectLanguage('¿Cómo está usted?')).toBe('es')
    expect(detectLanguage('¡Hola!')).toBe('es')
    // A Spanish diacritic is a strong marker in an EN/ES workbench.
    expect(detectLanguage('la reunión será mañana')).toBe('es')
    // Stopword density with no diacritics still resolves es (hola / por / la).
    expect(detectLanguage('hola por la ayuda')).toBe('es')
  })

  it('returns en for plain English text (stopword density, no Spanish markers)', () => {
    expect(detectLanguage('Hello, how are you today?')).toBe('en')
    expect(detectLanguage('the meeting is tomorrow morning')).toBe('en')
  })

  it('returns null for empty / numeric / signal-less text (caller falls back to the configured source)', () => {
    expect(detectLanguage('')).toBeNull()
    expect(detectLanguage('   ')).toBeNull()
    expect(detectLanguage('12345')).toBeNull()
    expect(detectLanguage('...')).toBeNull()
    // "no" is common to BOTH languages → no discriminating signal → null (NOT a false-positive 'es').
    expect(detectLanguage('no problem')).toBeNull()
    // A genuine ES/EN density tie (es 'y' vs en 'the') → null (exercises the tie branch, not just no-words).
    expect(detectLanguage('y the')).toBeNull()
  })
})
