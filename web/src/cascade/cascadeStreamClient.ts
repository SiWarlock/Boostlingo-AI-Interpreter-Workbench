import { sessionStore } from '../state/sessionStore'
import type { SessionStore } from '../state/sessionStore'
import type {
  CostEstimate,
  LanguageDirection,
  LatencyEvent,
  ProviderError,
  TranscriptSegment,
  TurnStatus,
  UiError,
} from '../types/domain'

// The cascade streaming WebSocket client (ARCH-009 / ARCH-011). Owns all wire detail: opens
// WS /api/cascade/stream, sends `start` -> binary PCM frames -> `stop`, and dispatches inbound server
// frames into the store. Clean separation (ARCH-007): components never import this; raw audio goes ONLY
// to the onAudio callback (-> D.5 playback), NEVER the store (invariant #3). The injectable wsFactory +
// location make the lifecycle unit-testable against a fake socket; only the real network is manual-smoke.

const WS_PATH = '/api/cascade/stream'

export type CascadeStartParams = {
  sessionId: string
  turnId: string
  direction: LanguageDirection
  sampleRate: number
  translationModel: string
  ttsVoice: string
}

export type CascadeAudioChunk = { contentType: string; seq: number; base64: string }

// The subset of the store the cascade client + dispatch drive (the streaming actions).
export type CascadeStore = Pick<
  SessionStore,
  'appendTranscriptSegment' | 'appendLatencyEvent' | 'setTurnCost' | 'failTurn' | 'completeTurn'
>

export type CascadeStreamClient = {
  start: (params: CascadeStartParams) => void
  sendFrame: (frame: ArrayBufferLike) => void
  stop: () => void
  close: () => void
}

type WsFactory = (url: string) => WebSocket
type WsLocation = { protocol: string; host: string }

// '' base -> derive ws(s) from the page location (dev proxy); an http(s) base -> swap to ws(s).
export function toWebSocketUrl(apiBase: string, location: WsLocation): string {
  if (apiBase === '') {
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:'
    return `${scheme}//${location.host}${WS_PATH}`
  }
  // Strip a trailing slash before appending the path so a configured base with one doesn't double it.
  return `${apiBase.replace(/\/$/, '').replace(/^http/, 'ws')}${WS_PATH}`
}

export function buildStartFrame(params: CascadeStartParams) {
  return {
    type: 'start' as const,
    sessionId: params.sessionId,
    turnId: params.turnId,
    direction: params.direction,
    encoding: 'linear16' as const,
    sampleRate: params.sampleRate,
    translationModel: params.translationModel,
    ttsVoice: params.ttsVoice,
  }
}

// Project the WS `error` frame's ProviderError to the lean client-facing UiError (drops provider +
// httpStatusCode) — mirrors the backend ErrorSanitizer projection (server lesson §13).
function providerErrorToUiError(error: ProviderError): UiError {
  return {
    code: error.code,
    safeMessage: error.safeMessage,
    stage: error.stage,
    retryable: error.retryable,
  }
}

// Parse a server text frame and route it to the store / onAudio. Returns the routed message type, or
// null when the frame is malformed / unknown (ignored — never throws). Pure (no socket state).
export function dispatchCascadeMessage(
  rawText: string,
  deps: { store: CascadeStore; onAudio: (chunk: CascadeAudioChunk) => void },
): string | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(rawText)
  } catch {
    return null
  }
  if (typeof parsed !== 'object' || parsed === null) {
    return null
  }
  const msg = parsed as Record<string, unknown>
  // The switch body accesses as-cast sub-fields; a well-formed frame with a known type but a missing
  // sub-object would throw past this router and stall the turn — guard it like the JSON.parse above.
  try {
    switch (msg.type) {
      case 'transcript':
        deps.store.appendTranscriptSegment(msg.segment as TranscriptSegment)
        return 'transcript'
      case 'latency':
        deps.store.appendLatencyEvent(msg.event as LatencyEvent)
        return 'latency'
      case 'audio':
        // Raw audio -> the playback callback ONLY (never the store/persistence, invariant #3).
        deps.onAudio({
          contentType: String(msg.contentType),
          seq: Number(msg.seq) || 0,
          base64: String(msg.base64),
        })
        return 'audio'
      case 'cost':
        deps.store.setTurnCost(msg.estimate as CostEstimate)
        return 'cost'
      case 'error':
        deps.store.failTurn(providerErrorToUiError(msg.error as ProviderError))
        return 'error'
      case 'done':
        deps.store.completeTurn(String(msg.turnId), msg.status as TurnStatus)
        return 'done'
      default:
        return null
    }
  } catch {
    return null
  }
}

const CONNECTION_LOST: UiError = {
  code: 'cascade.connection_lost',
  safeMessage: 'The connection was lost. Please retry the turn.',
  retryable: true,
}

export function createCascadeStreamClient(deps: {
  store: CascadeStore
  onAudio: (chunk: CascadeAudioChunk) => void
  wsFactory?: WsFactory
  location?: WsLocation
}): CascadeStreamClient {
  const wsFactory = deps.wsFactory ?? ((url: string) => new WebSocket(url))
  const location = deps.location ?? globalThis.location
  let ws: WebSocket | null = null
  // Set on `done` (normal end) AND on the first failure; gates the abnormal-close handler so a
  // post-done close is silent and an error-then-close fails the turn only once.
  let terminal = false
  // Capture begins before the socket opens (D.4b), so frames can arrive while CONNECTING. Queue them
  // until open, then flush after the start frame — never send on a not-open socket, never drop.
  let isOpen = false
  let pending: ArrayBufferLike[] = []
  // Idempotent stop: deferred to onopen if stopped while CONNECTING (sending on a not-open socket
  // throws InvalidStateError); a second stop() is a no-op.
  let stopped = false

  function failIfLive(): void {
    if (!terminal) {
      terminal = true
      deps.store.failTurn(CONNECTION_LOST)
    }
  }

  // Detach handlers + close the current socket so a stale socket (a deliberate close, or a re-start
  // for the next turn) can't fire failIfLive against the new turn.
  function teardown(): void {
    if (ws) {
      ws.onopen = null
      ws.onmessage = null
      ws.onerror = null
      ws.onclose = null
      ws.close()
      ws = null
    }
    isOpen = false
  }

  function start(params: CascadeStartParams): void {
    teardown() // drop any prior socket (e.g. a previous turn) before opening a new one
    terminal = false
    pending = []
    stopped = false
    const base = import.meta.env.VITE_API_BASE_URL ?? ''
    ws = wsFactory(toWebSocketUrl(base, location))
    ws.binaryType = 'arraybuffer'
    ws.onopen = () => {
      isOpen = true
      ws?.send(JSON.stringify(buildStartFrame(params)))
      for (const frame of pending) {
        ws?.send(frame)
      }
      pending = []
      if (stopped) {
        ws?.send(JSON.stringify({ type: 'stop' })) // a stop requested while CONNECTING, deferred to here
      }
    }
    ws.onmessage = (event: MessageEvent) => {
      if (typeof event.data !== 'string') {
        return // binary inbound is not part of the contract; ignore
      }
      const type = dispatchCascadeMessage(event.data, { store: deps.store, onAudio: deps.onAudio })
      // Both `done` (normal) and a server `error` frame are terminal — the server closes after either,
      // so the following close must NOT surface a spurious cascade.connection_lost.
      if (type === 'done' || type === 'error') {
        terminal = true
      }
    }
    ws.onerror = () => failIfLive()
    ws.onclose = () => failIfLive()
  }

  function sendFrame(frame: ArrayBufferLike): void {
    if (isOpen && ws) {
      ws.send(frame)
    } else {
      pending.push(frame) // queued until open (capture starts before the socket opens)
    }
  }

  function stop(): void {
    if (stopped) {
      return // idempotent — a double stop sends one frame
    }
    stopped = true
    if (isOpen && ws) {
      ws.send(JSON.stringify({ type: 'stop' }))
    }
    // else: deferred — onopen sends the stop once the socket opens (never send on a CONNECTING socket)
  }

  function close(): void {
    // A deliberate teardown (e.g. unmount): detach handlers first so it doesn't surface connection_lost.
    teardown()
  }

  return { start, sendFrame, stop, close }
}

// Production singleton — one client reused across turns. Construction opens NO socket (start() does).
// The audio sink is a settable delegate (default no-op): D.5 calls setAudioSink(playbackController.enqueue)
// at the composition root, so the singleton needn't be reconstructed to wire playback.
let audioSink: (chunk: CascadeAudioChunk) => void = () => {
  /* no playback sink wired yet */
}

export function setAudioSink(sink: (chunk: CascadeAudioChunk) => void): void {
  audioSink = sink
}

export const cascadeStreamClient = createCascadeStreamClient({
  store: sessionStore,
  onAudio: (chunk) => audioSink(chunk),
})
