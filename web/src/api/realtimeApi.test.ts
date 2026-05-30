import { afterEach, describe, expect, it, vi } from 'vitest'
import { realtimeApi } from './realtimeApi'
import { ApiError } from './http'
import type { RealtimeTokenRequest } from '../types/domain'

afterEach(() => {
  vi.unstubAllGlobals()
})

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

const tokenResponse = {
  clientSecret: 'ek_test_abc123',
  expiresAt: '2026-05-29T12:10:00+00:00',
  model: 'gpt-realtime',
}

describe('realtimeApi.mintClientSecret', () => {
  it('POSTs JSON to /api/realtime/client-secret with the full request body', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(tokenResponse))
    vi.stubGlobal('fetch', fetchMock)

    const req: RealtimeTokenRequest = {
      sessionId: 'session_abc',
      direction: { source: 'en', target: 'es' },
      model: 'gpt-realtime',
    }
    await realtimeApi.mintClientSecret(req)

    const [url, init] = fetchMock.mock.calls[0]
    const reqInit = init as RequestInit
    expect(url).toBe('/api/realtime/client-secret')
    expect(reqInit.method).toBe('POST')
    expect(new Headers(reqInit.headers).get('Content-Type')).toBe('application/json')
    expect(JSON.parse(reqInit.body as string)).toEqual({
      sessionId: 'session_abc',
      direction: { source: 'en', target: 'es' },
      model: 'gpt-realtime',
    })
  })

  it('resolves the parsed token response (clientSecret/expiresAt/model)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(tokenResponse))
    vi.stubGlobal('fetch', fetchMock)

    const result = await realtimeApi.mintClientSecret({
      sessionId: 'session_abc',
      direction: { source: 'en', target: 'es' },
    })

    expect(result).toEqual(tokenResponse)
    expect(result.clientSecret).toBe('ek_test_abc123')
  })

  it('omits the model key from the body when model is undefined', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(tokenResponse))
    vi.stubGlobal('fetch', fetchMock)

    await realtimeApi.mintClientSecret({
      sessionId: 'session_abc',
      direction: { source: 'en', target: 'es' },
    })

    const [, init] = fetchMock.mock.calls[0]
    const body = JSON.parse((init as RequestInit).body as string) as Record<string, unknown>
    expect('model' in body).toBe(false)
    expect(body).toEqual({ sessionId: 'session_abc', direction: { source: 'en', target: 'es' } })
  })

  it('surfaces a non-OK status as ApiError, never leaking the raw body', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse({ secret: 'sk-should-never-surface' }, 500))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await realtimeApi.mintClientSecret({
        sessionId: 'session_abc',
        direction: { source: 'en', target: 'es' },
      })
    } catch (e) {
      caught = e
    }
    expect(caught).toBeInstanceOf(ApiError)
    expect((caught as ApiError).uiError.code).toBe('http.500')
    expect((caught as ApiError).uiError.safeMessage).not.toContain('sk-')
  })
})
