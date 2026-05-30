import { afterEach, describe, expect, it, vi } from 'vitest'
import { sessionsApi } from './sessionsApi'
import { ApiError } from './http'
import type { CreateSessionRequest, InterpretationSession, LatencyEvent } from '../types/domain'

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
      translationModel: 'gpt-5.4-nano',
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
      translationModel: 'gpt-5.4-nano',
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
})
