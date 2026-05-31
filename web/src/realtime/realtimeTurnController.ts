import { ApiError } from '../api/http'
import { sessionsApi } from '../api/sessionsApi'
import { sessionStore } from '../state/sessionStore'
import type { SessionStore } from '../state/sessionStore'
import type { CompleteTurnRequest, LatencyEvent } from '../types/domain'
import { createRealtimeEventSink } from './realtimeEventSink'
import { normalizeRealtimeEvent, parseRealtimeEvent } from './realtimeEvents'
import type { RealtimeUsageTokens } from './realtimeEvents'
import { realtimeConnectionManager } from './realtimeConnectionManager'
import type { RealtimeConnectionManager } from './realtimeConnectionManager'
import { realtimeWebRtcClient } from './realtimeWebRtcClient'
import type { RealtimeWebRtcClient } from './realtimeWebRtcClient'

// The realtime turn controller (ARCH-010 §7 manual VAD-off) — the §11 cascade-recording analogue. DI'd +
// unit-tested vs the real store + mocked client/api; the production singleton wires the real collaborators.
// Manual turns are BUFFER-DELIMITED: the mic track streams continuously (E.3 addTrack), Start clears the
// input buffer + Stop commits it and asks for a response — there is NO per-turn mic start/stop. Lazy connect
// is the E.4b interim; E.5 hoists to a persistent pc at session-start + teardown + reconnect.

type Clock = () => string

// Re-asserted in the client session.update so a partial audio.input doesn't clobber the broker mint's
// input.transcription (Finding 2c/053 — else the SOURCE transcript never arrives). Mirrors the backend
// default RealtimeOptions.TranscriptionModel ("gpt-4o-transcribe", ARCH-010); keep the two in sync.
const REALTIME_INPUT_TRANSCRIPTION_MODEL = 'gpt-4o-transcribe'

// Server-VAD config for Phase-I auto mode (ARCH-010 §7) — GA defaults, surfaced as named constants for
// tuning. In auto mode the OpenAI server auto-detects speech start/end + auto-creates responses.
const SERVER_VAD_THRESHOLD = 0.5
const SERVER_VAD_PREFIX_PADDING_MS = 300
const SERVER_VAD_SILENCE_DURATION_MS = 500

export type RealtimeTurnDeps = {
  store: Pick<
    SessionStore,
    | 'getState'
    | 'beginTurn'
    | 'appendLatencyEvent'
    | 'appendTranscriptSegment'
    | 'failTurn'
    | 'completeTurn'
    | 'addError'
  >
  client: Pick<RealtimeWebRtcClient, 'sendClientEvent' | 'onServerEvent'>
  // Persistent connect is delegated here (E.5a) — the manager holds one pc across turns (idempotent).
  connectionManager: Pick<RealtimeConnectionManager, 'ensureConnected'>
  api: {
    createTurn: (sessionId: string) => Promise<{ turnId: string }>
    appendTurnEvents: (
      sessionId: string,
      turnId: string,
      events: LatencyEvent[],
    ) => Promise<unknown>
    completeTurn: (sessionId: string, turnId: string, body: CompleteTurnRequest) => Promise<unknown>
  }
  clock: Clock
}

export type RealtimeTurnController = {
  startTurn: () => Promise<void>
  stopTurn: () => void
}

export function createRealtimeTurnController(deps: RealtimeTurnDeps): RealtimeTurnController {
  const { store, client, connectionManager, api, clock } = deps
  // Guards a re-entrant start (web §11). Connect is now persistent — delegated to the connection manager
  // (one pc held across turns, idempotent), no longer a per-turn lazy-connect.
  let inFlight = false

  // A browser-clock turn-lifecycle marker (stage 'overall'; relativeMs is a placeholder — the top-level
  // latency deltas use absolute timestamps, never relativeMs; lesson §13 / the recordingActions precedent).
  function marker(name: string): LatencyEvent {
    return {
      name,
      stage: 'overall',
      timestamp: clock(),
      relativeMs: 0,
      clockSource: 'browser',
      metadata: {},
    }
  }

  // On responseDone the turn is finalized (moved into turns[]) — report its accumulated client events to the
  // backend (E.4a `appendTurnEvents`). A report failure is surfaced (never swallowed), sanitized (ARCH-018).
  function reportTurnEvents(sessionId: string, turnId: string): void {
    // Called AFTER sink.handle(responseDone) in the same synchronous onServerEvent tick — the sink's
    // completeTurn has already moved the turn into turns[], so it's readable here. A stale/failed turn
    // (completeTurn skipped) yields empty events (bounded — failTurn already surfaced it; the
    // responseDone+error interleave is E.5's ordering concern).
    const finalized = store.getState().turns.find((t) => t.turnId === turnId)
    const events = finalized?.latencyEvents ?? []
    void api.appendTurnEvents(sessionId, turnId, events).catch((error: unknown) => {
      store.addError(
        error instanceof ApiError
          ? error.uiError
          : {
              code: 'realtime.report_failed',
              safeMessage: 'Could not report turn metrics.',
              retryable: true,
            },
      )
    })
  }

  // On responseDone, also FINALIZE the realtime turn on the backend at /complete (053-C2b) — a SIBLING to
  // reportTurnEvents (independent backend writes, order-free). Forward the exact DC audio-token usage so the
  // backend prices the realtime cost from it (an absent field is OMITTED — never a synthesized 0 — so the
  // backend's disclosed-unavailable path runs, web §25). A failure is surfaced (sanitized, ARCH-018), never
  // swallowed. The cost is read back via GET /session (web §21), never through the store (invariant #3).
  function finalizeTurn(
    sessionId: string,
    turnId: string,
    usage: RealtimeUsageTokens | null,
  ): void {
    const body: CompleteTurnRequest = { status: 'completed' }
    if (usage?.inputAudioTokens !== undefined) body.inputAudioTokens = usage.inputAudioTokens
    if (usage?.outputAudioTokens !== undefined) body.outputAudioTokens = usage.outputAudioTokens
    if (usage?.cachedAudioInputTokens !== undefined) {
      body.cachedAudioInputTokens = usage.cachedAudioInputTokens
    }
    void api.completeTurn(sessionId, turnId, body).catch((error: unknown) => {
      store.addError(
        error instanceof ApiError
          ? error.uiError
          : {
              code: 'realtime.complete_failed',
              safeMessage: 'Could not finalize the turn.',
              retryable: true,
            },
      )
    })
  }

  async function startTurn(): Promise<void> {
    if (inFlight) {
      return
    }
    const { sessionId, direction } = store.getState()
    if (sessionId === null) {
      return // no active session
    }
    inFlight = true
    try {
      let turnId: string
      try {
        const created = await api.createTurn(sessionId)
        turnId = created.turnId
      } catch (error) {
        store.addError(
          error instanceof ApiError
            ? error.uiError
            : {
                code: 'turn.create_failed',
                safeMessage: 'Could not start the turn.',
                retryable: true,
              },
        )
        return // abort before any client wiring
      }

      store.beginTurn({ turnId, mode: 'realtime', direction })

      // Persistent connect: the manager holds one pc across turns (idempotent), stamps connecting/connected,
      // and surfaces disconnects. The first turn connects; subsequent turns reuse the live pc. A failed
      // connect fails + aborts THIS turn (the manager reset its latch so a later turn can retry).
      try {
        await connectionManager.ensureConnected()
      } catch (error) {
        store.failTurn(
          error instanceof ApiError
            ? error.uiError
            : {
                code: 'realtime.connect',
                safeMessage:
                  'Could not establish the realtime voice connection. Retry, or switch to Cascade.',
                retryable: true,
              },
        )
        return
      }

      // A fresh per-turn sink (E.4a) wired to the client's server-event stream: raw frame → parse → normalize
      // → sink; on responseDone the turn is finalized, so report its events to the backend.
      const sink = createRealtimeEventSink({ store, clock })
      // Per-turn latch (reset on each startTurn → a fresh turn after a settled one works). In Phase-I AUTO
      // mode the turn is single-utterance (slice 1): after the first auto response.done we go IDLE so a 2nd
      // server-VAD'd utterance can't re-finalize (re-POST /complete+/events) or garble turn 1 against a gone
      // currentTurn. Multi-segment (a turn per server-detected segment) is slice 2.
      let settled = false
      let warnedPostSettled = false
      // Replaces any prior turn's delegate — sinks are per-turn (the old closure is GC'd); only the active
      // controller writes onServerEvent (the inFlight guard prevents a concurrent rebind).
      client.onServerEvent = (raw) => {
        if (settled) {
          // DEV-only observability (manual-smoke-exempt, like the 053-B DC logger): make the documented
          // single-utterance boundary visible — a 2nd server-VAD'd utterance is dropped here (its audio
          // still plays via the media track, but no transcript/cost/events) until slice 2 handles
          // multi-segment. Warn ONCE per turn so a live auto-VAD smoke catches it instead of reading "broken".
          if (import.meta.env.DEV && !warnedPostSettled) {
            warnedPostSettled = true
            console.warn(
              '[realtime auto-VAD] ignoring server events after the first auto turn — slice 1 is ' +
                'single-utterance per Start; continuous multi-utterance is Phase-I slice 2.',
            )
          }
          return
        }
        const event = normalizeRealtimeEvent(parseRealtimeEvent(raw))
        if (event === null) {
          return
        }
        sink.handle(event)
        if (event.kind === 'responseDone') {
          reportTurnEvents(sessionId, turnId)
          finalizeTurn(sessionId, turnId, event.usage)
          if (store.getState().turnControlMode === 'auto') {
            settled = true
          }
        }
      }

      // Manual VAD-off (ARCH-010 §7): disable server turn detection, then clear the input buffer to delimit
      // the turn start. The mic track is already streaming (E.3 addTrack) — no per-turn mic toggle.
      // Re-assert input transcription in the SAME frame (Finding 053): this partial audio.input would
      // otherwise clobber the broker mint's input.transcription → no SOURCE transcript.
      // Phase I: in AUTO mode the server VADs (auto-detects speech start/end + auto-creates responses) via
      // `turn_detection: server_vad`; in MANUAL mode the turn is buffer-delimited (`turn_detection: null` +
      // Stop commits). 053-B: re-assert `transcription` in the SAME frame regardless (else the source
      // transcript regresses). The mode is gated mid-turn (canToggleMode), so it's stable across the turn.
      client.sendClientEvent({
        type: 'session.update',
        session: {
          audio: {
            input: {
              turn_detection:
                store.getState().turnControlMode === 'auto'
                  ? {
                      type: 'server_vad',
                      threshold: SERVER_VAD_THRESHOLD,
                      prefix_padding_ms: SERVER_VAD_PREFIX_PADDING_MS,
                      silence_duration_ms: SERVER_VAD_SILENCE_DURATION_MS,
                    }
                  : null,
              transcription: { model: REALTIME_INPUT_TRANSCRIPTION_MODEL },
            },
          },
        },
      })
      client.sendClientEvent({ type: 'input_audio_buffer.clear' })
      store.appendLatencyEvent(marker('turn.recording.started'))
    } finally {
      inFlight = false
    }
  }

  function stopTurn(): void {
    // Guard: only an active (started) turn can be stopped — a Stop before/without a turn is a no-op (avoids
    // commit/response.create on an unconnected channel + a stranded recording.stopped marker).
    if (store.getState().currentTurn === undefined) {
      return
    }
    if (store.getState().turnControlMode === 'auto') {
      // Auto-VAD (Phase I, slice 1): the SERVER owns segmentation (speech-stop → auto-commit → auto-response),
      // so Stop is a no-op — sending commit/response.create here would RACE the server's auto-commit → a
      // double response. The turn finalizes on the server's auto response.done. A guarded early-end override
      // (only if no `committed` seen) is slice 2. The realtime metrics speech-end anchor in auto mode is a
      // slice-2 concern (the user's Stop is not the speech-end here).
      return
    }
    // Manual: commit the buffered audio + ask for a response (ARCH-010 §7); stamp the speech-end marker.
    client.sendClientEvent({ type: 'input_audio_buffer.commit' })
    client.sendClientEvent({ type: 'response.create' })
    store.appendLatencyEvent(marker('turn.recording.stopped'))
  }

  return { startTurn, stopTurn }
}

// Production singleton — wires the real store/client/api (clock = wall clock). The realtime audio OUTPUT
// (remote track → <audio>) is wired at the realtimeWebRtcClient construction site (E.4b interim playback).
export const realtimeTurnController = createRealtimeTurnController({
  store: sessionStore,
  client: realtimeWebRtcClient,
  connectionManager: realtimeConnectionManager,
  api: {
    createTurn: (sessionId) => sessionsApi.createTurn(sessionId),
    appendTurnEvents: (sessionId, turnId, events) =>
      sessionsApi.appendTurnEvents(sessionId, turnId, events),
    completeTurn: (sessionId, turnId, body) => sessionsApi.completeTurn(sessionId, turnId, body),
  },
  clock: () => new Date().toISOString(),
})
