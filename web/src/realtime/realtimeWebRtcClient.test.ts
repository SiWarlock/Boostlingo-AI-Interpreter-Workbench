import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/http'
import {
  createRealtimeWebRtcClient,
  exchangeSdpOffer,
  realtimeCallsUrl,
} from './realtimeWebRtcClient'

afterEach(() => {
  vi.unstubAllGlobals()
})

const OFFER_SDP = 'v=0\r\no=- 0 0 IN IP4 0.0.0.0\r\ns=-\r\n...offer...'
const ANSWER_SDP = 'v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=-\r\n...answer...'

function sdpResponse(body: string, status = 200): Response {
  return new Response(body, { status, headers: { 'Content-Type': 'application/sdp' } })
}

describe('realtimeCallsUrl', () => {
  it('is the GA calls endpoint with NO ?model= query', () => {
    expect(realtimeCallsUrl).toBe('https://api.openai.com/v1/realtime/calls')
    expect(realtimeCallsUrl).not.toContain('model=')
  })
})

describe('exchangeSdpOffer', () => {
  it('POSTs the raw offer SDP with the ephemeral Bearer and application/sdp content-type', async () => {
    const fetchMock = vi.fn().mockResolvedValue(sdpResponse(ANSWER_SDP))
    vi.stubGlobal('fetch', fetchMock)

    await exchangeSdpOffer(OFFER_SDP, 'ek_test_abc123')

    const [url, init] = fetchMock.mock.calls[0]
    const reqInit = init as RequestInit
    expect(url).toBe('https://api.openai.com/v1/realtime/calls')
    expect(reqInit.method).toBe('POST')
    expect(new Headers(reqInit.headers).get('Authorization')).toBe('Bearer ek_test_abc123')
    expect(new Headers(reqInit.headers).get('Content-Type')).toBe('application/sdp')
    expect(reqInit.body).toBe(OFFER_SDP)
  })

  it('resolves the answer SDP as text (not JSON-parsed)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(sdpResponse(ANSWER_SDP))
    vi.stubGlobal('fetch', fetchMock)

    const answer = await exchangeSdpOffer(OFFER_SDP, 'ek_test_abc123')
    expect(answer).toBe(ANSWER_SDP)
  })

  it('surfaces a non-OK 4xx as a typed sanitized, non-retryable failure (never the raw body)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(sdpResponse('upstream error: sk-leak-me', 400))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await exchangeSdpOffer(OFFER_SDP, 'ek_test_abc123')
    } catch (e) {
      caught = e
    }
    expect(caught).toBeInstanceOf(ApiError)
    expect((caught as ApiError).uiError.code).toBe('realtime.connect')
    expect((caught as ApiError).uiError.safeMessage).not.toContain('sk-')
    // A 4xx config error is not transient — not retryable (pins the derivation the impl comment promises).
    expect((caught as ApiError).uiError.retryable).toBe(false)
  })

  it('classifies a 5xx handshake failure as retryable', async () => {
    const fetchMock = vi.fn().mockResolvedValue(sdpResponse('upstream unavailable', 503))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await exchangeSdpOffer(OFFER_SDP, 'ek_test_abc123')
    } catch (e) {
      caught = e
    }
    expect(caught).toBeInstanceOf(ApiError)
    expect((caught as ApiError).uiError.code).toBe('realtime.connect')
    expect((caught as ApiError).uiError.retryable).toBe(true)
  })

  it('surfaces a fetch rejection as a typed failure (never a raw TypeError)', async () => {
    const fetchMock = vi.fn().mockRejectedValue(new TypeError('network down'))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await exchangeSdpOffer(OFFER_SDP, 'ek_test_abc123')
    } catch (e) {
      caught = e
    }
    expect(caught).toBeInstanceOf(ApiError)
    expect(caught).not.toBeInstanceOf(TypeError)
    expect((caught as ApiError).uiError.code).toBe('realtime.connect')
    expect((caught as ApiError).uiError.retryable).toBe(true)
  })
})

// P0 072 — the DC-open gate at the client boundary. A client event sent while the DataChannel is
// `connecting` previously called dc.send() → InvalidStateError → startTurn rejected → realtime dead.
// sendClientEvent now routes through the queue (buffer-until-open) and flushes on the DC `onopen`. This
// pins the LOAD-BEARING wiring (a missing onopen→flush = the session.update never reaches the server =
// still-broken, same class as the bug) — over a fake pc/DC so it stays deterministic (the queue logic
// itself is unit-pinned in realtimeClientEventQueue.test.ts).
function fakeWebRtc() {
  const dc = {
    readyState: 'connecting' as RTCDataChannelState,
    onopen: null as (() => void) | null,
    onmessage: null as ((e: MessageEvent) => void) | null,
    send: vi.fn(),
    close: vi.fn(),
  }
  const pc = {
    connectionState: 'connecting',
    onconnectionstatechange: null,
    ontrack: null,
    createDataChannel: () => dc,
    addTrack: vi.fn(),
    createOffer: async () => ({ sdp: 'v=0\r\n...offer...' }),
    setLocalDescription: async () => {},
    setRemoteDescription: async () => {},
    close: vi.fn(),
  }
  const stream = { getAudioTracks: () => [{ stop: vi.fn() }], getTracks: () => [{ stop: vi.fn() }] }
  const deps = {
    mint: vi.fn().mockResolvedValue({ clientSecret: 'ek_test_abc' }),
    onRemoteTrack: vi.fn(),
    getUserMedia: vi.fn().mockResolvedValue(stream as unknown as MediaStream),
    createPeerConnection: () => pc as unknown as RTCPeerConnection,
  }
  return { deps, dc }
}

describe('createRealtimeWebRtcClient — sendClientEvent DC-open gate (P0 072)', () => {
  it('buffers a send while the DC is `connecting` (NOT thrown, NOT sent) and flushes it on the DC onopen', async () => {
    const { deps, dc } = fakeWebRtc()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('answer-sdp', { status: 200 })))
    const client = createRealtimeWebRtcClient(deps)
    await client.connect()

    // DC is `connecting` → the old `dataChannel?.send(...)` would throw InvalidStateError here; the queue
    // buffers it instead — no throw, nothing reaches dc.send yet.
    expect(() => client.sendClientEvent({ type: 'session.update' })).not.toThrow()
    expect(dc.send).not.toHaveBeenCalled()

    // the DC opens → the buffered session.update is flushed (the load-bearing onopen→flush wiring)
    dc.readyState = 'open'
    dc.onopen?.()
    expect(dc.send).toHaveBeenCalledWith(JSON.stringify({ type: 'session.update' }))
  })
})
