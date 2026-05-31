import { afterEach, describe, expect, it, vi } from 'vitest'
import { sessionsApi } from './sessionsApi'
import { ApiError } from './http'
import type {
  CreateSessionRequest,
  InterpretationSession,
  LatencyEvent,
  SessionListItem,
} from '../types/domain'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

const wireSession: InterpretationSession = {
  sessionId: 'session_abc',
  startedAt: '2026-05-29T12:00:00+00:00',
  config: {
    currentMode: 'cascade',
    direction: { source: 'en', target: 'es' },
    providerProfile: {
      realtimeProvider: 'openai',
      realtimeModel: 'gpt-realtime',
      sttProvider: 'deepgram',
      sttModel: 'nova-3',
      sttLanguage: 'multi',
      translationProvider: 'openai',
      translationModel: 'gpt-5-nano',
      ttsProvider: 'openai',
      ttsModel: 'gpt-4o-mini-tts',
      ttsVoice: 'alloy',
    },
  },
  turns: [],
  modeTransitions: [],
  pricingConfigVersion: '2026-05-28-payg-estimates',
}

describe('sessionsApi', () => {
  it('createSession POSTs JSON to /api/sessions and parses the InterpretationSession', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(wireSession))
    vi.stubGlobal('fetch', fetchMock)

    const body: CreateSessionRequest = {
      label: 'demo',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
      realtimeModel: 'gpt-realtime',
      translationModel: 'gpt-5-nano',
    }
    const result = await sessionsApi.createSession(body)

    const [url, init] = fetchMock.mock.calls[0]
    const reqInit = init as RequestInit
    expect(url).toBe('/api/sessions')
    expect(reqInit.method).toBe('POST')
    expect(new Headers(reqInit.headers).get('Content-Type')).toBe('application/json')
    expect(JSON.parse(reqInit.body as string)).toEqual(body)
    expect(result.sessionId).toBe('session_abc') // reads top-level sessionId (not `id`)
  })

  it('getSession GETs /api/sessions/{id}', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(wireSession))
    vi.stubGlobal('fetch', fetchMock)

    await sessionsApi.getSession('session_abc')

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/sessions/session_abc')
    expect((init as RequestInit).method).toBe('GET')
  })

  it('createTurn POSTs /api/sessions/{id}/turns and parses the turnId', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ turnId: 'turn_001' }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await sessionsApi.createTurn('session_abc')

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/sessions/session_abc/turns')
    expect((init as RequestInit).method).toBe('POST')
    expect(result.turnId).toBe('turn_001')
  })

  it('endSession POSTs /api/sessions/{id}/end and parses the EndSessionResponse', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse({ session: wireSession, persistedPath: 'session_abc.json' }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await sessionsApi.endSession('session_abc')

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/sessions/session_abc/end')
    expect((init as RequestInit).method).toBe('POST')
    expect(result.session.sessionId).toBe('session_abc')
    expect(result.persistedPath).toBe('session_abc.json')
  })

  it('getSummary GETs /api/sessions/{id}/summary and parses the SessionSummary', async () => {
    const summary = {
      turnCount: 1,
      cascade: {
        turnCount: 1,
        avgSpeechEndToFirstAudioMs: null, // n/a for cascade — the backend has no client-timing for it
        avgSpeechEndToPlaybackMs: null,
        estimatedCostPerMinuteUsd: 0.42,
        errorCount: 0,
        avgSttFinalMs: 120,
        avgTranslationFinalMs: 240,
        avgTtsFirstAudioMs: 360,
      },
      computedAt: '2026-05-29T12:00:00+00:00',
      pricingConfigVersion: '2026-05-28-payg-estimates',
    }
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(summary))
    vi.stubGlobal('fetch', fetchMock)

    const result = await sessionsApi.getSummary('session_abc')

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/sessions/session_abc/summary')
    expect((init as RequestInit).method).toBe('GET')
    expect(result.cascade?.estimatedCostPerMinuteUsd).toBe(0.42)
  })

  it('appendTurnEvents POSTs {events} to /api/sessions/{id}/turns/{turnId}/events', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ turnId: 'turn_001' }))
    vi.stubGlobal('fetch', fetchMock)

    const event: LatencyEvent = {
      name: 'realtime.first_audio_delta',
      stage: 'realtime',
      timestamp: '2026-05-29T12:00:00.000+00:00',
      relativeMs: 0,
      clockSource: 'browser',
      metadata: {},
    }
    await sessionsApi.appendTurnEvents('session_abc', 'turn_001', [event])

    const [url, init] = fetchMock.mock.calls[0]
    const reqInit = init as RequestInit
    expect(url).toBe('/api/sessions/session_abc/turns/turn_001/events')
    expect(reqInit.method).toBe('POST')
    expect(new Headers(reqInit.headers).get('Content-Type')).toBe('application/json')
    expect(JSON.parse(reqInit.body as string)).toEqual({ events: [event] }) // AppendEventsRequest wire shape
  })

  it('completeTurn POSTs the CompleteTurnRequest body to /api/sessions/{id}/turns/{turnId}/complete (053-C2b)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ turn: { turnId: 'turn_001' } }))
    vi.stubGlobal('fetch', fetchMock)

    const body = {
      status: 'completed',
      inputAudioTokens: 31,
      outputAudioTokens: 54,
      cachedAudioInputTokens: 0,
    } as const
    await sessionsApi.completeTurn('session_abc', 'turn_001', body)

    const [url, init] = fetchMock.mock.calls[0]
    const reqInit = init as RequestInit
    expect(url).toBe('/api/sessions/session_abc/turns/turn_001/complete')
    expect(reqInit.method).toBe('POST')
    expect(new Headers(reqInit.headers).get('Content-Type')).toBe('application/json')
    expect(JSON.parse(reqInit.body as string)).toEqual(body)
  })

  it('completeTurn surfaces a non-OK status as ApiError', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ code: 'turn.not_found' }, 404))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await sessionsApi.completeTurn('session_abc', 'turn_001', { status: 'completed' })
    } catch (e) {
      caught = e
    }
    expect(caught).toBeInstanceOf(ApiError)
  })

  it('appendTurnEvents surfaces a non-OK status as ApiError', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ code: 'turn.not_found' }, 404))
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await sessionsApi.appendTurnEvents('session_abc', 'turn_001', [])
    } catch (e) {
      caught = e
    }
    expect(caught).toBeInstanceOf(ApiError)
  })

  it('listSessions GETs /api/sessions and parses the SessionListItem[] (H.3 — a bare array)', async () => {
    const items: SessionListItem[] = [
      {
        sessionId: 'session_2',
        label: 'Recent run',
        startedAt: '2026-05-31T10:00:00+00:00',
        endedAt: '2026-05-31T10:05:00+00:00',
        turnCount: 3,
        modes: ['realtime', 'cascade'],
      },
      {
        sessionId: 'session_1',
        label: null,
        startedAt: '2026-05-30T09:00:00+00:00',
        endedAt: null,
        turnCount: 1,
        modes: ['cascade'],
      },
    ]
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(items)) // bare array, NOT { sessions: [] }
    vi.stubGlobal('fetch', fetchMock)

    const result = await sessionsApi.listSessions()

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/sessions')
    expect((init as RequestInit).method).toBe('GET')
    expect(result).toHaveLength(2)
    expect(result[0].sessionId).toBe('session_2') // backend order (most-recent-first) preserved verbatim
    expect(result[0].modes).toEqual(['realtime', 'cascade'])
  })

  it('listSessions surfaces the backend sessions.read_failed 500 as ApiError (the §35 read-fail boundary)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      // a real sanitized backend UiError body (code + safeMessage + retryable) — the §3 boundary surfaces
      // a well-formed UiError verbatim (vs the generic http.<status> for ProblemDetails/non-UiError bodies)
      jsonResponse(
        { code: 'sessions.read_failed', safeMessage: 'Could not read sessions.', retryable: false },
        500,
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await sessionsApi.listSessions()
    } catch (e) {
      caught = e
    }
    expect(caught).toBeInstanceOf(ApiError)
    expect((caught as ApiError).uiError.code).toBe('sessions.read_failed')
  })
})
