import type { LanguageCode } from '../types/domain'

// Deterministic best-effort EN/ES language heuristic (Phase J / J.3). Used ONLY to stamp a DISPLAY
// direction badge for realtime turns, where the provider emits no explicit language tag — so this is
// best-effort, NOT a measured signal. (Cascade rides the backend-resolved `{type:"direction"}` message,
// which derives the source language from Deepgram's streaming detection — a measured signal.)
//
// Returns null when the text carries no usable signal, so the CALLER falls back to the configured source
// (the "fall back to the configured source on ambiguity" policy lives at the call site, not here — keeping
// this pure function honest about what it can and can't tell).

// Inverted punctuation — unambiguously Spanish.
const SPANISH_PUNCTUATION = /[¿¡]/
// Spanish-specific letters (accented vowels + ñ/ü) — a strong Spanish marker in an EN/ES workbench.
const SPANISH_DIACRITICS = /[áéíóúñü]/i

// Common function words / greetings — a keyword-density tiebreaker for text with no strong marker. Words
// that are ALSO common English ('no', 'si') are deliberately excluded: they carry no discriminating signal
// in an EN/ES context (Spanish has plenty of exclusive markers) and would false-positive short English
// phrases like "no problem". Diacritic'd forms (señor, sí) are caught by the strong-marker path above.
const SPANISH_WORDS = new Set([
  'el',
  'la',
  'los',
  'las',
  'un',
  'una',
  'unos',
  'unas',
  'y',
  'o',
  'de',
  'del',
  'que',
  'en',
  'por',
  'para',
  'con',
  'sin',
  'su',
  'sus',
  'es',
  'soy',
  'eres',
  'somos',
  'hola',
  'gracias',
  'pero',
  'porque',
  'muy',
  'usted',
  'ayuda',
])
const ENGLISH_WORDS = new Set([
  'the',
  'a',
  'an',
  'and',
  'or',
  'of',
  'to',
  'in',
  'on',
  'for',
  'with',
  'is',
  'are',
  'am',
  'you',
  'he',
  'she',
  'it',
  'we',
  'they',
  'this',
  'that',
  'hello',
  'thanks',
  'how',
  'where',
  'when',
  'why',
  'very',
  'but',
  'because',
  'good',
  'please',
])

export function detectLanguage(text: string): LanguageCode | null {
  // Strong markers win immediately — inverted punctuation or a Spanish diacritic is decisive.
  if (SPANISH_PUNCTUATION.test(text) || SPANISH_DIACRITICS.test(text)) {
    return 'es'
  }
  // Otherwise count function-word membership; the denser language wins, a tie / no signal → null.
  const words = text.toLowerCase().match(/[a-z]+/g) ?? []
  let es = 0
  let en = 0
  for (const word of words) {
    if (SPANISH_WORDS.has(word)) es += 1
    if (ENGLISH_WORDS.has(word)) en += 1
  }
  if (es > en) return 'es'
  if (en > es) return 'en'
  return null
}
