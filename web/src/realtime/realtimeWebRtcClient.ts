import { ApiError } from '../api/http'
import type { RealtimeTokenResponse, UiError } from '../types/domain'
import { normalizeRealtimeEvent, parseRealtimeEvent } from './realtimeEvents'
import type { NormalizedRealtimeEvent } from './realtimeEvents'

// The browser Realtime WebRTC transport (ARCH-010 §7). Two layers:
//   1. PURE, unit-tested handshake seams — `realtimeCallsUrl` + `exchangeSdpOffer` (fetch-mock'd).
//   2. `createRealtimeWebRtcClient` — a MANUAL-SMOKE-EXEMPT shell wiring RTCPeerConnection + the `oai-events`
//      data channel + getUserMedia/addTrack + the SDP handshake (browser-internal per the root CLAUDE.md TDD
//      posture; verified by the ARCH-020 demo-checklist with a real OpenAI key, deferred).
// Clean separation (ARCH-007): components never import this; it owns all WebRTC wire detail. SAFETY
// (invariant #2): the ephemeral `ek_…` is a transient LOCAL here — used only as the SDP-exchange Bearer —
// NEVER returned to the store layer, persisted, or logged.

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
  close: () => void
}

export type RealtimeWebRtcDeps = {
  // Mints a fresh ephemeral secret from our backend (realtimeApi.mintClientSecret bound with the session).
  mint: () => Promise<RealtimeTokenResponse>
  // Normalized inbound GA events from the `oai-events` data channel (E.4 stamps latency + dispatches).
  onEvent: (event: NormalizedRealtimeEvent) => void
  // Remote translated-audio track (E.4/E.5 route to playback).
  onRemoteTrack: (stream: MediaStream) => void
  // Injectable for the (deferred) smoke harness; default to the browser globals.
  getUserMedia?: (constraints: MediaStreamConstraints) => Promise<MediaStream>
  createPeerConnection?: () => RTCPeerConnection
}

// Build a single Realtime WebRTC session shell (ARCH-010 §7 handshake). E.4 drives manual turns over the
// returned data channel; E.5 owns the persistent-pc-across-turns lifecycle + teardown + re-mint. This
// foundation slice only stands the shell up — it is consumer-pending (wired by E.4/E.5).
export function createRealtimeWebRtcClient(deps: RealtimeWebRtcDeps): RealtimeWebRtcClient {
  const getUserMedia =
    deps.getUserMedia ?? ((constraints) => navigator.mediaDevices.getUserMedia(constraints))
  const createPeerConnection = deps.createPeerConnection ?? (() => new RTCPeerConnection())

  let pc: RTCPeerConnection | null = null
  let micStream: MediaStream | null = null

  async function connect(): Promise<void> {
    // Invariant #2: the ek_ is a transient local — used only as the exchange Bearer below, never stored.
    const { clientSecret } = await deps.mint()

    micStream = await getUserMedia({ audio: true })
    pc = createPeerConnection()

    // Remote (translated) audio arrives via ontrack (ARCH-010 §7 step 7).
    pc.ontrack = (event: RTCTrackEvent) => {
      const [stream] = event.streams
      if (stream) {
        deps.onRemoteTrack(stream)
      }
    }

    // Server events arrive on the `oai-events` data channel (ARCH-010 §7 step 2).
    const dataChannel = pc.createDataChannel('oai-events')
    dataChannel.onmessage = (event: MessageEvent) => {
      if (typeof event.data !== 'string') {
        return
      }
      const normalized = normalizeRealtimeEvent(parseRealtimeEvent(event.data))
      if (normalized) {
        deps.onEvent(normalized)
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

  // Teardown discipline (ARCH-010 §7): stop tracks, close the pc, release the stream. E.5 extends this for
  // ICE-disconnect surfacing + the across-turns lifecycle.
  function close(): void {
    if (micStream) {
      for (const track of micStream.getTracks()) {
        track.stop()
      }
      micStream = null
    }
    if (pc) {
      pc.close()
      pc = null
    }
  }

  return { connect, close }
}
