import type { ConfigResponse, TurnStatus } from '../types/domain'

// Pure gating/availability derivations over the GET /api/config response (ARCH-007 / ARCH-017
// Flow A). Components render the result + dispatch intents; the gating logic is unit-tested here in
// isolation. Total over `undefined` config (pre-bootstrap render is safe).

export type ModeAvailability = { realtime: boolean; cascade: boolean }
export type AvailableModels = { realtimeModels: string[]; translationModels: string[] }

// A mode is enabled only when its provider keys are present. Cascade needs the FULL
// STT -> Translation -> TTS pipeline configured (a missing stage can't run a turn).
export function modeAvailability(config: ConfigResponse | undefined): ModeAvailability {
  if (!config) {
    return { realtime: false, cascade: false }
  }
  const cascade =
    config.cascade.stt.configured &&
    config.cascade.translation.configured &&
    config.cascade.tts.configured
  return { realtime: config.realtime.configured, cascade }
}

// Selectable model catalogs. The backend ConfigService ALWAYS populates the model lists; `configured`
// is key-presence only and gates the MODE (modeAvailability), not the catalog. So this reads the
// lists straight through — the only empty case is a not-yet-loaded (undefined) config. (Gating on
// `configured` would empty the selector when a key is absent and break create — both models are
// [Required] on CreateSessionRequest.)
export function availableModels(config: ConfigResponse | undefined): AvailableModels {
  if (!config) {
    return { realtimeModels: [], translationModels: [] }
  }
  return {
    realtimeModels: config.realtime.models,
    translationModels: config.cascade.translation.models,
  }
}

// ARCH-007 ModeToggle: mode switching is forbidden during recording/processing/playing.
const TOGGLE_BLOCKED = new Set<TurnStatus>(['recording', 'processing', 'playing'])
export function canToggleMode(turnStatus: TurnStatus): boolean {
  return !TOGGLE_BLOCKED.has(turnStatus)
}
