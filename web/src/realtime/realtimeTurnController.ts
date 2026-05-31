import { ApiError } from '../api/http'
import { sessionsApi } from '../api/sessionsApi'
import { sessionStore } from '../state/sessionStore'
import type { SessionStore } from '../state/sessionStore'
import type { CompleteTurnRequest, LanguageDirection, LatencyEvent, UiError } from '../types/domain'
import { createRealtimeEventSink } from './realtimeEventSink'
import type { RealtimeEventSink } from './realtimeEventSink'
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

// The session.update input.turn_detection value: server_vad (auto) or null (manual buffer-delimited).
type TurnDetection = {
  type: 'server_vad'
  threshold: number
  prefix_padding_ms: number
  silence_duration_ms: number
} | null

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
    | 'setTurnStatus'
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

  // Phase-I auto-VAD continuous-listening state (I.2 slice 2; web §27). In auto mode ONE Start opens a
  // continuous listening session and the server segments speech into MULTIPLE turns — so the per-segment
  // turn + sink live here (not as a single per-Start sink like manual). `autoListening` gates segment
  // creation + is cleared by close-listening Stop; `currentSegmentTurnId`/`currentSink` track the in-flight
  // segment (null between segments); `segmentStarting` guards the async createTurn window against a
  // duplicate/overlapping speech_started (sequential server-VAD makes overlap unlikely, but cheap to guard).
  let autoListening = false
  let currentSegmentTurnId: string | null = null
  let currentSink: RealtimeEventSink | null = null
  let segmentStarting = false
  // A speech-end (speech_stopped) timestamp captured while the segment's turn was still being created (the
  // async createTurn window): appendLatencyEvent needs a currentTurn, so the speech-end anchor can't land
  // yet — hold the TRUE speech-stop time here + stamp it when beginTurn settles (else a short segment whose
  // speech_stopped beats createTurn would silently lose its responsiveness anchor → n/a). Practically rare
  // (speech_stopped trails speech by >= silence_duration_ms, well past a local createTurn) but correctness-safe.
  let pendingRecordingStoppedTs: string | null = null

  // A browser-clock turn-lifecycle marker (stage 'overall'; relativeMs is a placeholder — the top-level
  // latency deltas use absolute timestamps, never relativeMs; lesson §13 / the recordingActions precedent).
  function marker(name: string, timestamp: string = clock()): LatencyEvent {
    return {
      name,
      stage: 'overall',
      timestamp,
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

  // Sanitized connect-failure UiError (ARCH-018) — shared by the manual + auto connect paths. An ApiError
  // carries its own sanitized uiError; anything else degrades to the fixed advise-switch message.
  function connectError(error: unknown): UiError {
    return error instanceof ApiError
      ? error.uiError
      : {
          code: 'realtime.connect',
          safeMessage:
            'Could not establish the realtime voice connection. Retry, or switch to Cascade.',
          retryable: true,
        }
  }

  // Build the session.update input config for a turn/segment. 053-B: re-assert `transcription` in the SAME
  // frame regardless of mode (else this partial audio.input clobbers the broker mint's input.transcription →
  // no SOURCE transcript). turn_detection branches: AUTO → server_vad (the server auto-detects speech
  // start/end + auto-creates responses); MANUAL → null (buffer-delimited; Stop commits). ARCH-010 §7.
  function sessionUpdateInput(turnDetection: TurnDetection): void {
    client.sendClientEvent({
      type: 'session.update',
      session: {
        audio: {
          input: {
            turn_detection: turnDetection,
            transcription: { model: REALTIME_INPUT_TRANSCRIPTION_MODEL },
          },
        },
      },
    })
  }

  async function startTurn(): Promise<void> {
    if (inFlight) {
      return
    }
    const { sessionId, direction, turnControlMode } = store.getState()
    if (sessionId === null) {
      return // no active session
    }
    inFlight = true
    try {
      if (turnControlMode === 'auto') {
        await startAutoSession(sessionId, direction)
      } else {
        await startManualTurn(sessionId, direction)
      }
    } finally {
      inFlight = false
    }
  }

  // MANUAL turn (ARCH-010 §7; web §17): buffer-delimited — createTurn → begin → connect → wire a single
  // per-turn sink → session.update(turn_detection:null) + clear; Stop commits + asks for a response. One
  // turn per Start (the slice-1 single-turn flow, minus the auto-only settled latch which moved to the
  // auto path's continuous-listening model).
  async function startManualTurn(sessionId: string, direction: LanguageDirection): Promise<void> {
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

    // Persistent connect: the manager holds one pc across turns (idempotent). A failed connect fails +
    // aborts THIS turn (the manager reset its latch so a later turn can retry).
    try {
      await connectionManager.ensureConnected()
    } catch (error) {
      store.failTurn(connectError(error))
      return
    }

    // A fresh per-turn sink (E.4a) wired to the client's server-event stream: raw → parse → normalize →
    // sink; on responseDone the turn is finalized, so report + finalize it to the backend.
    const sink = createRealtimeEventSink({ store, clock })
    client.onServerEvent = (raw) => {
      const event = normalizeRealtimeEvent(parseRealtimeEvent(raw))
      if (event === null) {
        return
      }
      sink.handle(event)
      if (event.kind === 'responseDone') {
        reportTurnEvents(sessionId, turnId)
        finalizeTurn(sessionId, turnId, event.usage)
      }
    }

    sessionUpdateInput(null)
    client.sendClientEvent({ type: 'input_audio_buffer.clear' })
    store.appendLatencyEvent(marker('turn.recording.started'))
  }

  // AUTO / server-VAD continuous-listening session (Phase I, I.2 slice 2; web §27). ONE Start opens the
  // listening session — the server segments speech into MULTIPLE turns (a turn per detected segment), so NO
  // eager turn is created here (no empty turn if the user never speaks). turnStatus → 'recording' opens the
  // UI (Start disables / Stop enables); the per-segment turns are born from `speech_started`.
  async function startAutoSession(sessionId: string, direction: LanguageDirection): Promise<void> {
    autoListening = true
    currentSegmentTurnId = null
    currentSink = null
    segmentStarting = false
    pendingRecordingStoppedTs = null
    store.setTurnStatus('recording')

    try {
      await connectionManager.ensureConnected()
    } catch (error) {
      autoListening = false
      store.failTurn(connectError(error))
      return
    }

    client.onServerEvent = (raw) => handleAutoServerEvent(raw, sessionId, direction)
    sessionUpdateInput({
      type: 'server_vad',
      threshold: SERVER_VAD_THRESHOLD,
      prefix_padding_ms: SERVER_VAD_PREFIX_PADDING_MS,
      silence_duration_ms: SERVER_VAD_SILENCE_DURATION_MS,
    })
    client.sendClientEvent({ type: 'input_audio_buffer.clear' })
  }

  // Begin a turn for a server-detected speech segment (async createTurn → beginTurn → fresh per-segment
  // sink → stamp recording.started). The turn exists before its transcript deltas arrive (deltas follow
  // speech_started by >= silence_duration_ms). Guarded against a duplicate/overlapping speech_started + a
  // close-listening (Stop) that lands during the async createTurn window.
  function beginAutoSegment(sessionId: string, direction: LanguageDirection): void {
    if (!autoListening || segmentStarting || currentSegmentTurnId !== null) {
      return
    }
    segmentStarting = true
    void api
      .createTurn(sessionId)
      .then(({ turnId }) => {
        if (!autoListening) {
          return // listening was closed (Stop) during createTurn — drop the segment cleanly
        }
        store.beginTurn({ turnId, mode: 'realtime', direction })
        currentSegmentTurnId = turnId
        currentSink = createRealtimeEventSink({ store, clock })
        store.appendLatencyEvent(marker('turn.recording.started'))
        // A speech_stopped that arrived during this createTurn window (before the turn existed) deferred its
        // true speech-end time here — stamp it now so the responsiveness anchor isn't lost (3A; §25/§13).
        if (pendingRecordingStoppedTs !== null) {
          store.appendLatencyEvent(marker('turn.recording.stopped', pendingRecordingStoppedTs))
          pendingRecordingStoppedTs = null
        }
      })
      .catch((error: unknown) => {
        store.addError(
          error instanceof ApiError
            ? error.uiError
            : {
                code: 'turn.create_failed',
                safeMessage: 'Could not start the turn.',
                retryable: true,
              },
        )
      })
      .finally(() => {
        segmentStarting = false
      })
  }

  // Route a server event in AUTO mode: speech_started begins a segment-turn; speech_stopped stamps the
  // speech-end anchor (3A — into turn.recording.stopped so deriveTurnMetrics + the backend session-avg
  // agree); committed is a lifecycle no-op; everything else (transcript deltas, audio, response.*) routes
  // to the current segment's sink, finalizing the segment on its response.done.
  function handleAutoServerEvent(
    raw: string,
    sessionId: string,
    direction: LanguageDirection,
  ): void {
    const event = normalizeRealtimeEvent(parseRealtimeEvent(raw))
    if (event === null) {
      return
    }
    switch (event.kind) {
      case 'speechStarted':
        beginAutoSegment(sessionId, direction)
        break
      case 'speechStopped':
        // 3A speech-end anchor: stamp turn.recording.stopped from the SERVER signal (there is no manual Stop
        // in auto) so deriveTurnMetrics anchors responsiveness on the real speech-end + reconciles with the
        // backend session-avg (both read recording.stopped; mirrors cascade §25/§13).
        if (currentSegmentTurnId !== null) {
          store.appendLatencyEvent(marker('turn.recording.stopped'))
        } else {
          // No segment turn yet (speech_started absent OR createTurn in flight) — hold the TRUE speech-end
          // time + apply it when the turn begins (on committed/response.created), so the anchor survives the
          // fallback begin path (Bug C, 070; previously gated on segmentStarting → lost without speech_started).
          pendingRecordingStoppedTs = clock()
        }
        break
      case 'committed':
        // Buffer auto-committed (053-C-CONFIRMED GA string). FALLBACK begin-trigger (Bug C, 070): if the
        // UNCONFIRMED `speech_started` never reached the controller, begin the segment here so the turn exists
        // by response.done → finalizes. Guarded (beginAutoSegment's segmentStarting/currentSegmentTurnId
        // check) → collapses to one begin when speech_started already fired.
        beginAutoSegment(sessionId, direction)
        break
      case 'responseCreated':
        // Last-resort FALLBACK begin-trigger (053-C-CONFIRMED) — belt-and-suspenders if neither
        // speech_started nor committed reached the controller. Guarded → one begin. (A lifecycle marker the
        // sink ignores anyway, so routing it here instead of to the sink loses nothing.)
        beginAutoSegment(sessionId, direction)
        break
      default: {
        // transcript deltas / audio / response.done / error → the current segment's sink.
        if (currentSink !== null) {
          currentSink.handle(event)
        }
        if (event.kind === 'responseDone' && currentSegmentTurnId !== null) {
          // The sink's completeTurn (above) moved the turn into turns[]; report + finalize it (§26 /complete
          // + /events per auto-turn), reset for the next segment, and re-arm 'recording' so the continuous
          // session keeps listening (instead of the per-turn 'completed' that would re-enable Start mid-session).
          const turnId = currentSegmentTurnId
          reportTurnEvents(sessionId, turnId)
          finalizeTurn(sessionId, turnId, event.usage)
          currentSegmentTurnId = null
          currentSink = null
          if (autoListening) {
            store.setTurnStatus('recording')
          }
        }
        break
      }
    }
  }

  function stopTurn(): void {
    if (store.getState().turnControlMode === 'auto') {
      // AUTO close-listening (I.2 slice 2): stop the server VAD (turn_detection:null) + return to a startable
      // state so the user can Start/End — NOT a commit/response.create (which would race the server's
      // auto-commit → a double response; slice-1 rule). The continuous listening session (re-armed between
      // segments) needs this control — otherwise only End could stop it. The guarded early-end of an in-flight
      // segment is deferred; if Stop lands mid-segment the server's own response.done still finalizes the turn.
      if (!autoListening) {
        return
      }
      autoListening = false
      sessionUpdateInput(null)
      store.setTurnStatus('completed')
      return
    }
    // Manual: only an active (started) turn can be stopped — a Stop before/without a turn is a no-op (avoids
    // commit/response.create on an unconnected channel + a stranded recording.stopped marker).
    if (store.getState().currentTurn === undefined) {
      return
    }
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
