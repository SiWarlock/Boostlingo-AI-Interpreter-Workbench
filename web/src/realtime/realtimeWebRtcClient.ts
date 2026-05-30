import { realtimeApi } from '../api/realtimeApi'
import { ApiError } from '../api/http'
import { sessionStore } from '../state/sessionStore'
import type { RealtimeTokenResponse, UiError } from '../types/domain'

// The browser Realtime WebRTC transport (ARCH-010 §7). Two layers:
//   1. PURE, unit-tested handshake seams — `realtimeCallsUrl` + `exchangeSdpOffer` (fetch-mock'd).
//   2. `createRealtimeWebRtcClient` — a MANUAL-SMOKE-EXEMPT shell: RTCPeerConnection + the `oai-events` data
//      channel + getUserMedia/addTrack + the SDP handshake (browser-internal per the root CLAUDE.md TDD
//      posture; verified by the ARCH-020 demo-checklist with a real OpenAI key, deferred). E.4b adds the DC
//      send/receive surface (`sendClientEvent` + a settable raw `onServerEvent`) — the controller's USE of
//      them is what's unit-tested (the normalize→sink wiring lives in the controller, not this shell).
// Clean separation (ARCH-007): components never import this; it owns all WebRTC wire detail. SAFETY
// (invariant #2): the ephemeral `ek_…` is a transient LOCAL — used only as the SDP-exchange Bearer — NEVER
// returned to the store layer, persisted, or logged. Invariant #3: the remote audio is a LIVE WebRTC track
// played directly, never captured/stored.

// GA realtime calls endpoint. The model is FIXED by the minted session — appending `?model=` returns HTTP
// 400 (ARCH-010 §7). Do NOT add a query string. Hardcoded: a third-party GA endpoint, not operator-
// configurable (no env override, unlike our-backend base URLs).
export const realtimeCallsUrl = 'https://api.openai.com/v1/realtime/calls'

// A fixed, sanitized failure for the handshake — never echoes the raw upstream SDP-error body (ARCH-018).
// E.3 keeps `retryable` uniform (advise-retry / switch-to-Cascade, ARCH-010); E.5 may status-derive it.
function sdpExchangeFailed(retryable: boolean): UiError {
  return {
    code: 'realtime.connect',
    safeMessage:
      'Could not establish the realtime voice connection. Please retry, or switch to Cascade.',
    retryable,
  }
}

// POST the local SDP offer to OpenAI's GA calls endpoint, authorized by the ephemeral `ek_…`, and return
// the answer SDP as TEXT (the exchange is application/sdp, not the JSON `request` boundary). A non-OK status
// or a fetch rejection surfaces as a typed ApiError(UiError) — never a raw body / TypeError leak (mirrors the
// `http` boundary, web lesson §3). The `ek` is used transiently in the header only (invariant #2).
export async function exchangeSdpOffer(offerSdp: string, ephemeralSecret: string): Promise<string> {
  let response: Response
  try {
    response = await fetch(realtimeCallsUrl, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${ephemeralSecret}`,
        'Content-Type': 'application/sdp',
      },
      body: offerSdp,
    })
  } catch {
    // Network down / fetch reject — synthesize, never propagate the TypeError.
    throw new ApiError(sdpExchangeFailed(true))
  }

  if (!response.ok) {
    // Never read/echo the raw upstream body (no-leak). 429/5xx are retryable; a 4xx config error is not.
    throw new ApiError(sdpExchangeFailed(response.status === 429 || response.status >= 500))
  }

  return response.text()
}

// ---- Manual-smoke shell (ARCH-020 demo-checklist; NOT unit-tested — browser WebRTC is exempt) ----

export type RealtimeWebRtcClient = {
  connect: () => Promise<void>
  // Teardown on End/mode-switch: close DC/pc, stop tracks, release the stream, detach the remote <audio>.
  teardown: () => void
  // Send a client control frame over the `oai-events` data channel (ARCH-010 §7: session.update,
  // input_audio_buffer.clear/commit, response.create). No-op if the channel isn't open yet.
  sendClientEvent: (event: object) => void
  // Settable per-turn delegate: raw inbound server frames (the controller does parse→normalize→sink).
  onServerEvent: ((raw: string) => void) | null
  // Settable delegate: the pc connectionState (`connected`/`failed`/`disconnected`) — the connection
  // manager maps it to the timing stamps + disconnect-surfacing.
  onConnectionState: ((state: string) => void) | null
}

export type RealtimeWebRtcDeps = {
  // Mints a fresh ephemeral secret from our backend (realtimeApi.mintClientSecret bound with the session).
  mint: () => Promise<RealtimeTokenResponse>
  // Remote translated-audio track (E.4b interim playback routes it to a detached <audio>).
  onRemoteTrack: (stream: MediaStream) => void
  // Injectable for the (deferred) smoke harness; default to the browser globals.
  getUserMedia?: (constraints: MediaStreamConstraints) => Promise<MediaStream>
  createPeerConnection?: () => RTCPeerConnection
}

// Build a single Realtime WebRTC session shell (ARCH-010 §7 handshake). E.4b drives manual turns over the
// data channel via `sendClientEvent`/`onServerEvent`; E.5 owns the persistent-pc-across-turns lifecycle +
// teardown + re-mint.
export function createRealtimeWebRtcClient(deps: RealtimeWebRtcDeps): RealtimeWebRtcClient {
  const getUserMedia =
    deps.getUserMedia ?? ((constraints) => navigator.mediaDevices.getUserMedia(constraints))
  const createPeerConnection = deps.createPeerConnection ?? (() => new RTCPeerConnection())

  let pc: RTCPeerConnection | null = null
  let micStream: MediaStream | null = null
  let dataChannel: RTCDataChannel | null = null

  const client: RealtimeWebRtcClient = {
    connect,
    teardown,
    sendClientEvent: (event) => dataChannel?.send(JSON.stringify(event)),
    onServerEvent: null,
    onConnectionState: null,
  }

  async function connect(): Promise<void> {
    // Invariant #2: the ek_ is a transient local — used only as the exchange Bearer below, never stored.
    const { clientSecret } = await deps.mint()

    micStream = await getUserMedia({ audio: true })
    pc = createPeerConnection()

    // Surface the connection state (ARCH-010 §7) — the connection manager maps connected/failed/disconnected
    // to the timing stamps + disconnect-surfacing.
    pc.onconnectionstatechange = () => {
      if (pc) {
        client.onConnectionState?.(pc.connectionState)
      }
    }

    // Remote (translated) audio arrives via ontrack (ARCH-010 §7 step 7).
    pc.ontrack = (event: RTCTrackEvent) => {
      const [stream] = event.streams
      if (stream) {
        deps.onRemoteTrack(stream)
      }
    }

    // Server events arrive on the `oai-events` data channel (ARCH-010 §7 step 2) — forwarded RAW to the
    // settable delegate (the controller owns parse→normalize→sink, so that wiring is unit-tested).
    dataChannel = pc.createDataChannel('oai-events')
    dataChannel.onmessage = (event: MessageEvent) => {
      if (typeof event.data === 'string') {
        client.onServerEvent?.(event.data)
      }
    }

    for (const track of micStream.getAudioTracks()) {
      pc.addTrack(track, micStream)
    }

    const offer = await pc.createOffer()
    await pc.setLocalDescription(offer)
    if (!offer.sdp) {
      // A populated local SDP is required for the exchange — surface a sanitized, non-retryable error
      // rather than POSTing an empty body (OpenAI 400s that with a confusing message).
      throw new ApiError(sdpExchangeFailed(false))
    }

    const answerSdp = await exchangeSdpOffer(offer.sdp, clientSecret)
    await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp })
  }

  // Teardown discipline (ARCH-010 §7): stop tracks, close the DC + pc, release the stream, detach the remote
  // <audio>, and clear the settable delegates. Called on End Session / mode-switch (E.5a/E.5b).
  function teardown(): void {
    if (micStream) {
      for (const track of micStream.getTracks()) {
        track.stop()
      }
      micStream = null
    }
    if (pc) {
      pc.onconnectionstatechange = null
      pc.ontrack = null
      pc.close()
      pc = null
    }
    dataChannel = null
    client.onServerEvent = null
    client.onConnectionState = null
    detachRemoteAudio()
  }

  return client
}

// ---- Production singleton + interim realtime audio output (E.4b; E.5 hardens reuse/teardown) ----

// The remote translated voice is a LIVE WebRTC track — play it directly via a detached <audio> (never
// captured/stored, invariant #3). Stamp `playback.started` on the `playing` event (browser clock): the
// realtime speech_end→playback timing, and the fallback first-audio marker if WebRTC emits no
// `output_audio.delta` on the DC (the E.4a smoke-uncertainty). All browser-internal (manual-smoke).
let remoteAudio: HTMLAudioElement | null = null

function attachRemoteAudio(stream: MediaStream): void {
  remoteAudio ??= new Audio()
  let stamped = false // stamp playback.started ONCE per attach — the `playing` event can re-fire (pause/resume)
  remoteAudio.onplaying = () => {
    if (stamped) {
      return
    }
    stamped = true
    sessionStore.appendLatencyEvent({
      name: 'playback.started',
      stage: 'playback',
      timestamp: new Date().toISOString(),
      relativeMs: 0,
      clockSource: 'browser',
      metadata: {},
    })
  }
  remoteAudio.srcObject = stream
  void remoteAudio.play()
}

// Detach + drop the remote <audio> on teardown (End/mode-switch) — the next attach gets a fresh element with
// a fresh playback.started once-guard (discharges the <audio>-lifecycle reset).
function detachRemoteAudio(): void {
  if (remoteAudio) {
    remoteAudio.onplaying = null
    remoteAudio.srcObject = null
    remoteAudio = null
  }
}

// One realtime client reused across turns (lazy connect via the controller). `mint` reads the live session
// from the store at connect time; the standard key never leaves the backend (invariant #1).
export const realtimeWebRtcClient = createRealtimeWebRtcClient({
  mint: () => {
    const state = sessionStore.getState()
    return realtimeApi.mintClientSecret({
      sessionId: state.sessionId ?? '',
      direction: state.direction,
      model: state.realtimeModel,
    })
  },
  onRemoteTrack: (stream) => attachRemoteAudio(stream),
})
