import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/http'
import { exchangeSdpOffer, realtimeCallsUrl } from './realtimeWebRtcClient'

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
