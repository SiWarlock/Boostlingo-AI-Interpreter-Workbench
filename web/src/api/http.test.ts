import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, request } from './http'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})

function jsonResponse(body: unknown, init?: ResponseInit): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })
}

describe('http.request', () => {
  it('builds the URL from VITE_API_BASE_URL + path, relative when base is empty', async () => {
    // mockImplementation (not mockResolvedValue) yields a FRESH Response per call — a Response body
    // is single-read, and this test issues two requests.
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ ok: true }))
    vi.stubGlobal('fetch', fetchMock)

    vi.stubEnv('VITE_API_BASE_URL', 'http://api.test')
    await request('/api/config')
    expect(fetchMock.mock.calls.at(-1)?.[0]).toBe('http://api.test/api/config')

    vi.stubEnv('VITE_API_BASE_URL', '')
    await request('/api/config')
    expect(fetchMock.mock.calls.at(-1)?.[0]).toBe('/api/config')
  })

  it('parses a 200 JSON body into the typed result', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ value: 42 }))
    vi.stubGlobal('fetch', fetchMock)
    const result = await request<{ value: number }>('/api/thing')
    expect(result).toEqual({ value: 42 })
  })

  it('throws a typed ApiError carrying the parsed UiError body on a non-OK response', async () => {
    const uiError = {
      code: 'session.not_found',
      safeMessage: 'Session not found.',
      retryable: false,
    }
    // Fresh Response per call (single-read body) — this test awaits the rejection twice.
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse(uiError, { status: 404 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(request('/api/sessions/missing')).rejects.toBeInstanceOf(ApiError)
    await expect(request('/api/sessions/missing')).rejects.toMatchObject({
      uiError: { code: 'session.not_found', retryable: false },
    })
  })

  it('synthesizes a generic UiError for a non-UiError error body without leaking the raw body', async () => {
    // ASP.NET ProblemDetails (framework validation 400) — a different shape than UiError; must tolerate.
    const problemDetails = {
      type: 'https://tools.ietf.org/html/rfc7231#section-6.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { RealtimeModel: ['SECRET-LOOKING-INTERNAL-DETAIL'] },
    }
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(problemDetails, { status: 400 }))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await request('/api/sessions')
    } catch (err) {
      caught = err
    }
    expect(caught).toBeInstanceOf(ApiError)
    const { uiError } = caught as ApiError
    expect(uiError.code).toBe('http.400')
    expect(uiError.retryable).toBe(false)
    // No raw body leaked into the surfaced message.
    expect(uiError.safeMessage).not.toContain('SECRET-LOOKING-INTERNAL-DETAIL')
    expect(JSON.stringify(uiError)).not.toContain('SECRET-LOOKING-INTERNAL-DETAIL')
  })

  it('maps a fetch rejection (network error / backend down) to a synthesized network UiError', async () => {
    // fetch itself rejects (TypeError) when the backend is unreachable — the most likely dev failure
    // (frontend started before backend). The helper must NEVER propagate a raw TypeError: the App
    // on-mount bootstrap's `catch -> store.addError(e.uiError)` requires an ApiError(UiError) always.
    const fetchMock = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await request('/api/config')
    } catch (err) {
      caught = err
    }
    expect(caught).toBeInstanceOf(ApiError)
    const { uiError } = caught as ApiError
    expect(uiError.code).toBe('network.error')
    expect(uiError.retryable).toBe(true)
    // No raw fetch/TypeError text leaked into the surfaced message.
    expect(uiError.safeMessage).not.toContain('Failed to fetch')
  })

  it('maps an unparseable OK body to a synthesized ApiError (never a raw SyntaxError)', async () => {
    // The success path must honor the same single-failure-boundary contract as the non-OK + reject
    // paths: a 2xx with a non-JSON/empty body must surface as an ApiError, not a raw SyntaxError.
    const fetchMock = vi.fn().mockResolvedValue(new Response('<<not json>>', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await request('/api/thing')
    } catch (err) {
      caught = err
    }
    expect(caught).toBeInstanceOf(ApiError)
    expect((caught as ApiError).uiError.code).toBe('response.invalid')
    expect((caught as ApiError).uiError.retryable).toBe(false)
  })
})
