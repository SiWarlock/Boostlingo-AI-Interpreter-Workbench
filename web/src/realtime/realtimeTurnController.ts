import { ApiError } from '../api/http'
import { sessionsApi } from '../api/sessionsApi'
import { sessionStore } from '../state/sessionStore'
import type { SessionStore } from '../state/sessionStore'
import type { LatencyEvent } from '../types/domain'
import { createRealtimeEventSink } from './realtimeEventSink'
import { normalizeRealtimeEvent, parseRealtimeEvent } from './realtimeEvents'
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
    appendTurnEvents: (sessionId: string, turnId: string, events: LatencyEvent[]) => Promise<unknown>
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
    return { name, stage: 'overall', timestamp: clock(), relativeMs: 0, clockSource: 'browser', metadata: {} }
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
          : { code: 'realtime.report_failed', safeMessage: 'Could not report turn metrics.', retryable: true },
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
            : { code: 'turn.create_failed', safeMessage: 'Could not start the turn.', retryable: true },
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
                safeMessage: 'Could not establish the realtime voice connection. Retry, or switch to Cascade.',
                retryable: true,
              },
        )
        return
      }

      // A fresh per-turn sink (E.4a) wired to the client's server-event stream: raw frame → parse → normalize
      // → sink; on responseDone the turn is finalized, so report its events to the backend.
      const sink = createRealtimeEventSink({ store, clock })
      // Replaces any prior turn's delegate — sinks are per-turn (the old closure is GC'd); only the active
      // controller writes onServerEvent (the inFlight guard prevents a concurrent rebind).
      client.onServerEvent = (raw) => {
        const event = normalizeRealtimeEvent(parseRealtimeEvent(raw))
        if (event === null) {
          return
        }
        sink.handle(event)
        if (event.kind === 'responseDone') {
          reportTurnEvents(sessionId, turnId)
        }
      }

      // Manual VAD-off (ARCH-010 §7): disable server turn detection, then clear the input buffer to delimit
      // the turn start. The mic track is already streaming (E.3 addTrack) — no per-turn mic toggle.
      client.sendClientEvent({
        type: 'session.update',
        session: { audio: { input: { turn_detection: null } } },
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
    // Commit the buffered audio + ask for a response (ARCH-010 §7); stamp the speech-end marker.
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
  },
  clock: () => new Date().toISOString(),
})
