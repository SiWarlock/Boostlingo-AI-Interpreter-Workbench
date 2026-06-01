import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSoakAudioCache, fetchDevTtsAudio } from './soakAudioCache'
import { ApiError } from '../api/http'

// The cached-audio loader (G.4 / decision 4 + Q3): synthesize each scripted utterance ONCE via the dev TTS
// endpoint, decode, and cache by (text, language) so the IDENTICAL audio is reused across both modes (fair +
// repeatable). The real fetch/decode is manual-smoke (browser `AudioContext.decodeAudioData`); the cache-key
// + fetch-once dedup + per-utterance orchestration is unit-TDD'd here with injected fakes.

const buffer = (): AudioBuffer => ({}) as AudioBuffer

describe('createSoakAudioCache', () => {
  it('fetches + decodes once per (text, language) key — a repeat is served from cache', async () => {
    const fetchAudioBytes = vi.fn().mockResolvedValue(new ArrayBuffer(8))
    const decodeAudio = vi.fn().mockResolvedValue(buffer())
    const cache = createSoakAudioCache({ fetchAudioBytes, decodeAudio })

    await cache.load('hola', 'es')
    await cache.load('hola', 'es')

    expect(fetchAudioBytes).toHaveBeenCalledTimes(1)
    expect(decodeAudio).toHaveBeenCalledTimes(1)
  })

  it('keys by BOTH text and language — same text, different language = distinct entries', async () => {
    const fetchAudioBytes = vi.fn().mockResolvedValue(new ArrayBuffer(8))
    const decodeAudio = vi.fn().mockResolvedValue(buffer())
    const cache = createSoakAudioCache({ fetchAudioBytes, decodeAudio })

    await cache.load('hello', 'en')
    await cache.load('hello', 'es')

    expect(fetchAudioBytes).toHaveBeenCalledTimes(2)
    expect(fetchAudioBytes).toHaveBeenNthCalledWith(1, 'hello', 'en')
    expect(fetchAudioBytes).toHaveBeenNthCalledWith(2, 'hello', 'es')
  })

  it('surfaces a decode failure + evicts so a retry re-fetches (no poisoned half-entry)', async () => {
    const fetchAudioBytes = vi.fn().mockResolvedValue(new ArrayBuffer(8))
    const decodeAudio = vi
      .fn()
      .mockRejectedValueOnce(new Error('bad audio'))
      .mockResolvedValueOnce(buffer())
    const cache = createSoakAudioCache({ fetchAudioBytes, decodeAudio })

    await expect(cache.load('hola', 'es')).rejects.toThrow('bad audio')
    // The failed key was evicted → a retry re-fetches + re-decodes (no poisoned half-entry served forever).
    await expect(cache.load('hola', 'es')).resolves.toBeDefined()
    expect(fetchAudioBytes).toHaveBeenCalledTimes(2)
    expect(decodeAudio).toHaveBeenCalledTimes(2)
  })
})

// The default real loader the cache uses (089 wiring). dev/tts returns raw WAV BYTES (Results.File), NOT
// JSON — so it bypasses the `http` helper (which reads `response.json()`). This pins the FE↔086 request
// contract at unit level (a drift from `POST /api/dev/tts {text,language}` fails here, not at the live run)
// and keeps the failure path on the no-leak boundary (web §3/§4). The real decode stays manual-smoke.
describe('fetchDevTtsAudio', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('POSTs the dev TTS contract (JSON body) and returns the audio bytes', async () => {
    const bytes = new ArrayBuffer(16)
    const fetchMock = vi
      .fn()
      .mockImplementation(() =>
        Promise.resolve(
          new Response(bytes, { status: 200, headers: { 'content-type': 'audio/wav' } }),
        ),
      )
    vi.stubGlobal('fetch', fetchMock)

    const result = await fetchDevTtsAudio('hola mundo', 'es')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/dev/tts')
    expect(init.method).toBe('POST')
    expect((init.headers as Record<string, string>)['content-type']).toBe('application/json')
    expect(JSON.parse(init.body as string)).toEqual({ text: 'hola mundo', language: 'es' })
    expect(result.byteLength).toBe(16)
  })

  it('surfaces a non-OK response as a sanitized ApiError (no raw body leak)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('boom', { status: 502 })))
    await expect(fetchDevTtsAudio('hi', 'en')).rejects.toBeInstanceOf(ApiError)
  })
})
