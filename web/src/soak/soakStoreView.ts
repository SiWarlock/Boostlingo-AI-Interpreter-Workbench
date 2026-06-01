import type { TurnViewModel } from '../types/domain'
import { deriveTurnMetrics } from '../state/selectors'
import type { SoakStoreView, SoakTurnObservation } from './soakRunner'

// The store→SoakTurnObservation adapter (089b) — the production side of the 089a `SoakStoreView` seam.
// Reads the real store `turns[]` (no bypass, ARCH-007) and projects each turn to the soak's observation.
// Pure over its injected reads, so it's TDD'd against a fake store state.

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
