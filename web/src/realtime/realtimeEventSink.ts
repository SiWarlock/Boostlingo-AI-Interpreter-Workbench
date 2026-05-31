import type { SessionStore } from '../state/sessionStore'
import type { LatencyEvent, LatencyStage, TranscriptSegment, UiError } from '../types/domain'
import type { NormalizedRealtimeEvent } from './realtimeEvents'

// Injected for deterministic tests; returns an ISO-8601 browser-clock timestamp.
type Clock = () => string

export type RealtimeEventSink = {
  handle(event: NormalizedRealtimeEvent): void
}

// The subset of the store the realtime data path drives (the mode-agnostic streaming actions, D.4a —
// reused verbatim from cascade). The sink takes ONLY what it calls; there is NO audio sink (invariant #3).
type RealtimeStore = Pick<
  SessionStore,
  'getState' | 'appendTranscriptSegment' | 'appendLatencyEvent' | 'failTurn' | 'completeTurn'
>

const REALTIME_PROVIDER = 'openai-realtime'

// A fixed, generic failure message — NEVER derived from the GA error code/payload (E.3 already dropped the
// raw provider message; E.5 builds the fuller ProviderError map). ARCH-018 no-leak.
const REALTIME_ERROR_MESSAGE =
  'The realtime session encountered an error. Please retry, or switch to Cascade.'

// Create a PER-TURN stateful sink (a fresh one per realtime turn — no reset() foot-gun). It maps E.3's
// NormalizedRealtimeEvents into the store:
//  - transcripts by role, ACCUMULATING incremental tokens into a cumulative running partial — the store's
//    §10 appendSegment REPLACES the trailing non-final partial (cascade-cumulative model), so the sink must
//    pass cumulative text or only the last token would render;
//  - first-of-type latency stamps (browser clock, once each);
//  - turn completion — the store's completeTurn (D.6) owns the turn.completed stamp, so the sink calls it
//    once and does NOT double-stamp — plus failure.
// `audioDelta` is TIMING-ONLY — it stamps first_audio_delta and writes NO audio to the store (invariant #3:
// realtime audio plays via the WebRTC media track / E.3 ontrack, never here; the base64 is discarded).
export function createRealtimeEventSink(deps: {
  store: RealtimeStore
  clock: Clock
}): RealtimeEventSink {
  const { store, clock } = deps
  // First-of-type latency markers (ARCH-010 metrics) are TARGET-transcript + audio only — the speech-end
  // basis uses first_audio_delta + first_transcript_delta(target); ARCH-010 defines no source first-delta
  // metric, so there is intentionally no firstSourceTranscriptStamped.
  let firstAudioStamped = false
  let firstTargetTranscriptStamped = false
  let sourceText = ''
  let targetText = ''
  // Synthetic segment ids — the store keeps only {text,isFinal} (§10), so this is type-satisfaction only.
  let segmentSeq = 0

  function stamp(name: string, stage: LatencyStage = 'realtime'): void {
    const event: LatencyEvent = {
      name,
      stage,
      timestamp: clock(),
      relativeMs: 0, // browser-clock marker; canonical deltas come from absolute timestamps (lesson §13)
      clockSource: 'browser',
      metadata: {},
    }
    store.appendLatencyEvent(event)
  }

  // First-audio is stamped ONCE per turn (the firstAudioStamped latch) by whichever audio marker fires
  // first. Under WebRTC that is `output_audio_buffer.started` (the DC event that fires); `audioDelta`
  // (response.output_audio.delta) is a latch-guarded FALLBACK for any env where the delta does arrive
  // (053-C1). Stamps both the realtime first-audio marker AND playback.started, PER TURN (A2, brief 049 —
  // never the session-persistent <audio> onplaying once-latch, which leaked a prior turn's stamp → negative
  // deltas; refines §17/§23). The detached <audio> still PLAYS the live track — it just no longer STAMPS.
  function stampFirstAudioOnce(): void {
    if (!firstAudioStamped) {
      firstAudioStamped = true
      stamp('realtime.first_audio_delta')
      stamp('playback.started', 'playback')
    }
  }

  function appendSegment(role: 'source' | 'target', text: string, isFinal: boolean): void {
    const segment: TranscriptSegment = {
      segmentId: `realtime-${role}-${++segmentSeq}`,
      role,
      text,
      isFinal,
      provider: REALTIME_PROVIDER,
      timestamp: clock(),
      clockSource: 'browser',
    }
    store.appendTranscriptSegment(segment)
  }

  function handle(event: NormalizedRealtimeEvent): void {
    switch (event.kind) {
      case 'targetTranscriptDelta':
        if (!firstTargetTranscriptStamped) {
          firstTargetTranscriptStamped = true
          stamp('realtime.first_transcript_delta')
        }
        targetText += event.text
        appendSegment('target', targetText, false)
        break
      case 'sourceTranscriptDelta':
        sourceText += event.text
        appendSegment('source', sourceText, false)
        break
      case 'sourceTranscriptCompleted':
        // The completed event carries the authoritative full transcript (E.3 reads `transcript`). Reset the
        // accumulator — the source stream is finalized, so a (rare) later delta starts a fresh partial
        // instead of concatenating onto the now-stale value (which would push a garbled cumulative).
        appendSegment('source', event.text, true)
        sourceText = ''
        break
      case 'outputAudioStarted':
        // The DC first-audio anchor under WebRTC (053-C1) — output_audio_buffer.started carries NO audio,
        // so the timing-only posture is strictly preserved (invariant #3).
        stampFirstAudioOnce()
        break
      case 'audioDelta':
        // TIMING ONLY — write NO audio/transcript (invariant #3; the base64 is discarded). Latch-guarded
        // FALLBACK first-audio anchor (response.output_audio.delta does not arrive on the DC under WebRTC).
        stampFirstAudioOnce()
        break
      case 'responseDone': {
        // response.done is the target-transcript-final signal (E.3 handoff): finalize the running target
        // partial (if any) BEFORE completing, so the final target rides into turns[]. Source finalizes on
        // its own sourceTranscriptCompleted. completeTurn (D.6) stamps turn.completed — no double-stamp here.
        if (targetText.length > 0) {
          appendSegment('target', targetText, true)
        }
        const turnId = store.getState().currentTurn?.turnId
        if (turnId) {
          store.completeTurn(turnId, 'completed')
        }
        break
      }
      case 'responseCreated':
        // Lifecycle marker — no store action (E.4b/E.5 may consume it).
        break
      case 'error': {
        const uiError: UiError = {
          code: event.code,
          safeMessage: REALTIME_ERROR_MESSAGE,
          retryable: false,
          stage: 'realtime',
        }
        store.failTurn(uiError)
        break
      }
    }
  }

  return { handle }
}
