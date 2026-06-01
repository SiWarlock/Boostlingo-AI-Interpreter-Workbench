import type { TurnViewModel } from '../types/domain'
import { deriveTurnMetrics } from '../state/selectors'
import type { SoakStoreView, SoakTurnObservation } from './soakRunner'

// The store→SoakTurnObservation adapter (089b) — the production side of the 089a `SoakStoreView` seam.
// Reads the real store `turns[]` (no bypass, ARCH-007) and projects each turn to the soak's observation.
// Pure over its injected reads, so it's TDD'd against a fake store state.

// Mirror the BE pricing constants — the disclosed bases the soak reuses for the per-turn output-audio
// duration (093). Realtime is DERIVED from the REPORTED tokens; cascade is the rougher §36 char→minutes
// estimate (disclosed via the report's overlapBasis). If the BE rates drift, a config-sync is the fix.
const REALTIME_TOKENS_PER_AUDIO_SECOND = 50 // server CostEstimator.cs RealtimeTokensPerAudioSecond
const TTS_APPROX_CHARS_PER_MINUTE = 900 // server CascadeWsMapping.cs TtsApproxCharsPerMinute

// Per-mode output-audio duration (ms) for the soak's overlap detection — or null when no signal exists.
// Realtime: output tokens ÷ tokens-per-second (the 092 reported count). Cascade: target-transcript chars
// (the TTS input text) ÷ chars-per-minute — the disclosed cost-grade estimate (overlapBasis discloses it).
export function resolveSoakOutputDurationMs(turn: TurnViewModel): number | null {
  if (turn.mode === 'realtime') {
    if (turn.outputAudioTokens === undefined || !Number.isFinite(turn.outputAudioTokens)) {
      return null // no reported tokens → honest null (overlap skips this pair)
    }
    return (turn.outputAudioTokens / REALTIME_TOKENS_PER_AUDIO_SECOND) * 1000
  }
  const targetChars = turn.targetTranscript.map((segment) => segment.text).join('').length
  if (targetChars === 0) {
    return null // no TTS text → nothing to estimate
  }
  return (targetChars / TTS_APPROX_CHARS_PER_MINUTE) * 60000
}

export type SoakStoreViewDeps = {
  getTurns: () => TurnViewModel[]
  // Absolute browser-clock ms at run start — the origin the schedule offsets + playback-end are relative to.
  runStartMs: number
  // Per-turn output-audio duration (ms) or null. No reliable per-turn output duration exists in the store
  // today (audioDurationMs is the INPUT/recording duration, 076; outputAudioDurationMs is never sent), so
  // the production resolver returns null → playbackEndMs null → overlap is disclosed-unmeasured (087).
  resolveOutputDurationMs: (turn: TurnViewModel) => number | null
}

export function createSoakStoreView(deps: SoakStoreViewDeps): SoakStoreView {
  function getCompletedTurns(): SoakTurnObservation[] {
    return deps.getTurns().map((turn, index) => {
      const metrics = deriveTurnMetrics(turn)
      const endToEndLatencyMs =
        typeof metrics.speechEndToFirstAudioMs === 'number' ? metrics.speechEndToFirstAudioMs : null
      const sourceTranscript = turn.sourceTranscript
        .map((segment) => segment.text)
        .join(' ')
        .trim()
      return {
        index,
        endToEndLatencyMs,
        playbackEndMs: derivePlaybackEndMs(turn, deps),
        sourceTranscript,
      }
    })
  }

  return { getCompletedTurns }
}

// Playback END (run-relative) = run-relative playback START + the output-audio duration — NOT the start
// (the overlap detector needs when turn N's output FINISHED). Null when there's no `playback.started`
// stamp or no resolvable output duration (honest: the overlap detector then skips that pair, 087).
function derivePlaybackEndMs(turn: TurnViewModel, deps: SoakStoreViewDeps): number | null {
  const started = (turn.latencyEvents ?? []).find((e) => e.name === 'playback.started')
  if (started === undefined) {
    return null
  }
  const startedAbsMs = Date.parse(started.timestamp)
  if (!Number.isFinite(startedAbsMs)) {
    return null
  }
  const outputDurationMs = deps.resolveOutputDurationMs(turn)
  if (outputDurationMs === null || !Number.isFinite(outputDurationMs)) {
    return null
  }
  return startedAbsMs - deps.runStartMs + outputDurationMs
}
