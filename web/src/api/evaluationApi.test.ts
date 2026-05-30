import { afterEach, describe, expect, it, vi } from 'vitest'
import { evaluationApi } from './evaluationApi'
import { ApiError } from './http'
import type { EvaluationPhrase, TranscribeResponse, WerRequest, WerResponse } from '../types/domain'

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

const wireWerResult = {
  phraseId: 'p1',
  reference: 'the quick brown fox',
  hypothesis: 'the quick brown fox',
  normalizedReference: 'the quick brown fox',
  normalizedHypothesis: 'the quick brown fox',
  substitutions: 0,
  insertions: 0,
  deletions: 0,
  referenceWordCount: 4,
  wer: 0,
}

describe('evaluationApi', () => {
  it('getPhrases GETs /api/evaluation/phrases and parses the EvaluationPhrase[]', async () => {
    const phrases: EvaluationPhrase[] = [
      { phraseId: 'p1', language: 'en', referenceText: 'the quick brown fox', category: 'pangram' },
    ]
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(phrases))
    vi.stubGlobal('fetch', fetchMock)

    const result = await evaluationApi.getPhrases()

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/evaluation/phrases')
    expect((init as RequestInit | undefined)?.method).toBe('GET')
    expect(result).toEqual(phrases)
  })

  it('transcribe POSTs multipart form-data with NO forced content-type and parses TranscribeResponse', async () => {
    const wire: TranscribeResponse = {
      hypothesis: 'the quick brown fox',
      sttProvider: 'deepgram',
      sttModel: 'nova-3',
      latencyEvents: [],
    }
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(wire))
    vi.stubGlobal('fetch', fetchMock)

    const audio = new Blob(['fake-audio-bytes'], { type: 'audio/webm' })
    const result = await evaluationApi.transcribe(
      { sessionId: 'session_abc', phraseId: 'p1', language: 'en' },
      audio,
    )

    const [url, init] = fetchMock.mock.calls[0]
    const reqInit = init as RequestInit
    expect(url).toBe('/api/evaluation/transcribe')
    expect(reqInit.method).toBe('POST')

    const form = reqInit.body as FormData
    expect(form).toBeInstanceOf(FormData)
    // fetch must set the multipart boundary itself — no forced (JSON or other) content-type header.
    expect(new Headers(reqInit.headers).get('Content-Type')).toBeNull()
    expect(form.get('sessionId')).toBe('session_abc')
    expect(form.get('phraseId')).toBe('p1')
    expect(form.get('language')).toBe('en')
    expect(form.get('audio')).toBeInstanceOf(Blob)

    expect(result.hypothesis).toBe('the quick brown fox')
    expect(result.sttModel).toBe('nova-3')
  })

  it('computeWer POSTs JSON to /api/evaluation/wer and parses WerResponse', async () => {
    const wire: WerResponse = { result: wireWerResult }
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(wire))
    vi.stubGlobal('fetch', fetchMock)

    const req: WerRequest = {
      sessionId: 'session_abc',
      turnId: 'turn_eval_1',
      phraseId: 'p1',
      hypothesis: 'the quick brown fox',
    }
    const result = await evaluationApi.computeWer(req)

    const [url, init] = fetchMock.mock.calls[0]
    const reqInit = init as RequestInit
    expect(url).toBe('/api/evaluation/wer')
    expect(reqInit.method).toBe('POST')
    expect(new Headers(reqInit.headers).get('Content-Type')).toBe('application/json')
    expect(JSON.parse(reqInit.body as string)).toEqual(req) // turnId carried (the persist path)
    expect(result.result.wer).toBe(0)
  })

  it('a non-OK status surfaces as ApiError via the shared http boundary', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(
        jsonResponse(
          { code: 'evaluation.phrase_not_found', safeMessage: 'not found', retryable: false },
          404,
        ),
      )
    vi.stubGlobal('fetch', fetchMock)

    let caught: unknown
    try {
      await evaluationApi.getPhrases()
    } catch (e) {
      caught = e
    }
    expect(caught).toBeInstanceOf(ApiError)
  })
})
