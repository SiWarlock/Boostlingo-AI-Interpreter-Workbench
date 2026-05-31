import type {
  ConfigResponse,
  CostEstimate,
  LatencyEvent,
  TurnStatus,
  TurnViewModel,
  UiSessionState,
} from '../types/domain'

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

// ARCH-007 recording-transition table. Start is allowed only when the session is started AND no turn
// is in flight; Stop only while recording.
const SESSION_CAN_RECORD = new Set<UiSessionState['sessionStatus']>(['active', 'readyForTurn'])
const TURN_CAN_START = new Set<TurnStatus>(['ready', 'completed', 'failed'])

export function canStartRecording(
  state: Pick<UiSessionState, 'sessionStatus' | 'turnStatus'>,
): boolean {
  return SESSION_CAN_RECORD.has(state.sessionStatus) && TURN_CAN_START.has(state.turnStatus)
}

export function canStopRecording(state: Pick<UiSessionState, 'turnStatus'>): boolean {
  return state.turnStatus === 'recording'
}

// --- D.6 metrics derivation (the load-bearing three-source model) ---------------------------------
// Per-stage latency comes from the store's `stages` map (server-computed relativeMs — passed through,
// NOT recomputed; lesson §7). Top-level client-timing deltas are computed HERE from the raw event
// timeline's absolute timestamps, because for cascade the backend structurally cannot (no
// client->server latency channel). Session averages come from GET /summary (backend-canonical), not here.

// Wall-clock millisecond difference between two events' absolute timestamps, or undefined if either
// endpoint is absent. Mirrors the backend MetricsAggregator.Between: deliberately NOT clamped — a small
// cross-clock negative is disclosed (ARCH-013), never hidden. relativeMs is never used for this math.
function between(from: LatencyEvent | undefined, to: LatencyEvent | undefined): number | undefined {
  if (!from || !to) {
    return undefined
  }
  return new Date(to.timestamp).getTime() - new Date(from.timestamp).getTime()
}

// A turn's display metrics: top-level deltas (absolute-timestamp Between over the raw timeline) + the
// per-stage passthrough. A metric whose endpoint event is absent stays undefined -> the panel renders n/a.
export function deriveTurnMetrics(turn: TurnViewModel): TurnViewModel['latency'] {
  const byName = new Map<string, LatencyEvent>()
  for (const event of turn.latencyEvents ?? []) {
    // First arrival wins (mirrors the orchestrator's first-arrival stamping); these markers occur once.
    if (!byName.has(event.name)) {
      byName.set(event.name, event)
    }
  }
  const recordingStarted = byName.get('turn.recording.started')
  const recordingStopped = byName.get('turn.recording.stopped')
  // Speech-end proxy for the RESPONSIVENESS metric (speech_end_to_first_audio_ms). ARCH-013 documents
  // turn.recording.stopped, BUT pre-VAD that is the MANUAL stop — held seconds after the user finished
  // speaking — so the responsiveness goes negative (056 bug 3). For cascade, stt.final (Deepgram
  // endpointing) is the hold-robust true-speech-end signal; fall back to recording.stopped when absent
  // (realtime has no stt.final → unchanged). Signed-off ARCH-013 realization note (056) — scoped to
  // first-audio ONLY; speech_end_to_playback_ms below keeps the literal recording.stopped anchor so it
  // stays consistent with the backend ModeSummary.avgSpeechEndToPlaybackMs.
  const responsivenessAnchor = byName.get('stt.final') ?? recordingStopped
  // turn.completed (browser-clock, stamped on the WS `done`) is the canonical totalTurn terminal;
  // tts.complete (server-clock) is the cross-clock fallback when turn.completed isn't present.
  const terminal = byName.get('turn.completed') ?? byName.get('tts.complete')
  // ARCH-013 selects the present output-audio event for speech_end_to_first_audio_ms:
  // tts.first_audio (cascade) ?? realtime.first_audio_delta (realtime) ?? playback.started, else n/a.
  // (A1, brief 049 — the realtime fallback was missing, so realtime turns showed a permanent n/a headline.)
  const firstAudio =
    byName.get('tts.first_audio') ??
    byName.get('realtime.first_audio_delta') ??
    byName.get('playback.started')
  return {
    speechEndToFirstAudioMs: between(responsivenessAnchor, firstAudio),
    speechEndToPlaybackMs: between(recordingStopped, byName.get('playback.started')),
    totalTurnMs: between(recordingStarted, terminal),
    stages: deriveStageDurations(byName),
  }
}

// Per-stage DURATIONS (ARCH-013 cascade stage metrics, line ~1051), differenced from the stage markers
// via absolute-timestamp Between — NOT the relativeMs passthrough. The store's `stages` map held
// {eventName: relativeMs-from-origin}, whose keys never matched the panel's stt/translation/tts AND whose
// values were not stage durations → the per-stage display read a permanent n/a (056 bug 1). A stage whose
// marker is absent is omitted (honest n/a, never a fabricated 0 — §13).
function deriveStageDurations(byName: Map<string, LatencyEvent>): Record<string, number> {
  const stages: Record<string, number> = {}
  const add = (key: string, fromName: string, toName: string): void => {
    const d = between(byName.get(fromName), byName.get(toName))
    // Omit an absent OR negative duration. Stage markers are all SERVER clock, so a negative is a
    // mis-stamp (NOT disclosed cross-clock skew) — render honest n/a, never a negative that poisons the
    // panel's stage-bar divisor, never a fabricated 0 (§13).
    if (d !== undefined && d >= 0) {
      stages[key] = d
    }
  }
  add('stt', 'cascade.audio.received', 'stt.final')
  add('translation', 'translation.started', 'translation.final')
  add('tts', 'tts.started', 'tts.complete')
  return stages
}

// Formats a USD-per-minute figure as the always-qualified "Estimated $X.XX/min" (ARCH-014); returns the
// shared 'n/a' token when unavailable (never a bare 0, never an error). The single source of the
// qualified per-minute string — reused for both per-turn (via formatCostPerMinute) and per-mode
// session figures (ModeSummary.estimatedCostPerMinuteUsd).
export function formatUsdPerMinute(value: number | null | undefined): string {
  // null/undefined/non-finite → the shared 'n/a' token (never a bare 0, never '$NaN/min'). The
  // !Number.isFinite guard closes a latent NaN/Infinity leak (§21 precedent). (074)
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return 'n/a'
  }
  // Sub-dime (0 < v < 0.10) renders 4 decimals so a sub-cent estimate keeps its signal (0.0116 → $0.0116,
  // not the toFixed(2) collapse to $0.01 — user "more defined", 074). An exact 0 is a REAL zero (≠ n/a,
  // §13) → $0.00 (2-decimal, NOT $0.0000). ≥$0.10 stays 2-decimal cents (regression-pin: $0.42/$1.00). (074)
  const decimals = value > 0 && value < 0.1 ? 4 : 2
  return `Estimated $${value.toFixed(decimals)}/min`
}

// Per-turn convenience over a CostEstimate. The model + assumptions for the tooltip the CostPanel reads
// off the estimate directly.
export function formatCostPerMinute(estimate: CostEstimate | null | undefined): string {
  return formatUsdPerMinute(estimate?.estimatedUsdPerMinute)
}
