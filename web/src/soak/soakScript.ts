import type { LanguageCode } from '../types/domain'

// The canonical scripted EN↔ES help-desk conversation for the G.4 5-min synthetic soak-harness
// (decision 3A). The TEXT is the committed source of truth + the known WER ground-truth (decision 4 — only
// the synthesized WAVs are gitignored). A traveler (EN) and a local (ES) alternate, so the bidirectional
// detect+flip path is exercised in BOTH directions across the run. The real per-utterance audio durations
// are decoded at runtime (088); this module only owns the text + the shape invariants.

export type SoakUtterance = {
  id: string
  sourceLang: LanguageCode
  text: string
}

export type SoakScript = {
  id: string
  // Inter-utterance gap (ms) at 1× real-time — includes the time the OTHER direction's translation plays
  // back, so a realistic help-desk cadence won't artificially trip the J.5 inter-turn reconnect.
  gapMs: number
  utterances: SoakUtterance[]
}

export type SoakScriptValidation = {
  valid: boolean
  issues: string[]
}

// "≈24 utterances" band (decision 3A). A nominal per-utterance speaking length used ONLY for the
// design-time cadence sanity-bound (the real durations come from decoded TTS at runtime).
const MIN_UTTERANCES = 20
const MAX_UTTERANCES = 28
const NOMINAL_UTTERANCE_MS = 5000
const MIN_CADENCE_MS = 180_000 // 3 min
const MAX_CADENCE_MS = 480_000 // 8 min

// 24 utterances, alternating EN/ES, ~8–12 words each — a neutral traveler ↔ local help-desk exchange.
export const CANONICAL_SOAK_SCRIPT: SoakScript = {
  id: 'help-desk-en-es-v1',
  gapMs: 6000,
  utterances: [
    {
      id: 'u01',
      sourceLang: 'en',
      text: 'Hello, I just arrived at the airport and need some help.',
    },
    {
      id: 'u02',
      sourceLang: 'es',
      text: 'Bienvenido a la ciudad, con mucho gusto le ayudo ahora.',
    },
    { id: 'u03', sourceLang: 'en', text: 'Where can I find a taxi to downtown from here?' },
    {
      id: 'u04',
      sourceLang: 'es',
      text: 'La parada de taxis está saliendo por la puerta número cuatro.',
    },
    { id: 'u05', sourceLang: 'en', text: 'How much should the ride to the city center cost?' },
    {
      id: 'u06',
      sourceLang: 'es',
      text: 'El viaje al centro cuesta aproximadamente veinticinco dólares con la propina.',
    },
    { id: 'u07', sourceLang: 'en', text: 'Is there a cheaper option like a train or bus?' },
    {
      id: 'u08',
      sourceLang: 'es',
      text: 'Sí, el tren urbano cuesta solo tres dólares por persona.',
    },
    { id: 'u09', sourceLang: 'en', text: 'Can you tell me where the nearest train station is?' },
    {
      id: 'u10',
      sourceLang: 'es',
      text: 'Está a dos cuadras, doblando a la derecha en la esquina.',
    },
    { id: 'u11', sourceLang: 'en', text: 'Thank you. Do I need exact change for the ticket?' },
    {
      id: 'u12',
      sourceLang: 'es',
      text: 'Las máquinas aceptan tarjetas, así que no necesita efectivo exacto.',
    },
    { id: 'u13', sourceLang: 'en', text: 'Great. I also need to find a hotel for tonight.' },
    {
      id: 'u14',
      sourceLang: 'es',
      text: 'Hay varios hoteles cómodos cerca de la estación central del tren.',
    },
    {
      id: 'u15',
      sourceLang: 'en',
      text: 'Which one would you recommend for a reasonable nightly price?',
    },
    {
      id: 'u16',
      sourceLang: 'es',
      text: 'El hotel Plaza ofrece buenas habitaciones a un precio muy justo.',
    },
    {
      id: 'u17',
      sourceLang: 'en',
      text: 'Does that hotel include breakfast and free wireless internet?',
    },
    {
      id: 'u18',
      sourceLang: 'es',
      text: 'Sí, incluye desayuno completo e internet gratis en todo el edificio.',
    },
    { id: 'u19', sourceLang: 'en', text: 'Perfect. How far is it to walk from the station?' },
    {
      id: 'u20',
      sourceLang: 'es',
      text: 'Son unos diez minutos caminando, todo derecho por la avenida principal.',
    },
    { id: 'u21', sourceLang: 'en', text: 'Is the neighborhood safe to walk around at night?' },
    {
      id: 'u22',
      sourceLang: 'es',
      text: 'Sí, es una zona muy tranquila y bien iluminada de noche.',
    },
    {
      id: 'u23',
      sourceLang: 'en',
      text: 'You have been incredibly helpful. Thank you so much today.',
    },
    {
      id: 'u24',
      sourceLang: 'es',
      text: 'Fue un placer ayudarle, que disfrute mucho de su viaje.',
    },
  ],
}

// Pure validator — pins the soak-script shape invariants so a future edit to the committed script can't
// silently break the harness (alternation, ≈24 count, non-empty text/id, both directions, ~5-min cadence).
export function validateSoakScript(script: SoakScript): SoakScriptValidation {
  const issues: string[] = []
  const { utterances, gapMs } = script
  const n = utterances.length

  if (n < MIN_UTTERANCES || n > MAX_UTTERANCES) {
    issues.push(`Expected ≈24 utterances (${MIN_UTTERANCES}–${MAX_UTTERANCES}); got ${n}.`)
  }

  const langs = new Set<LanguageCode>()
  for (let i = 0; i < n; i++) {
    const u = utterances[i]
    langs.add(u.sourceLang)
    if (u.id.trim() === '') {
      issues.push(`Utterance at index ${i} has an empty id.`)
    }
    if (u.text.trim() === '') {
      issues.push(`Utterance ${u.id || `#${i}`} has empty text.`)
    }
    if (i > 0 && u.sourceLang === utterances[i - 1].sourceLang) {
      issues.push(
        `Utterances ${i - 1}/${i} do not alternate source language (both ${u.sourceLang}).`,
      )
    }
  }
  if (n > 0 && (!langs.has('en') || !langs.has('es'))) {
    issues.push('Script must exercise both EN and ES source directions.')
  }

  const estimatedMs = n * NOMINAL_UTTERANCE_MS + Math.max(0, n - 1) * gapMs
  if (estimatedMs < MIN_CADENCE_MS || estimatedMs > MAX_CADENCE_MS) {
    issues.push(
      `Estimated cadence ${Math.round(estimatedMs / 1000)}s is outside the ~5-min soak window.`,
    )
  }

  return { valid: issues.length === 0, issues }
}
