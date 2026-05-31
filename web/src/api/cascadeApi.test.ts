import { afterEach, describe, expect, it, vi } from 'vitest'
import { cascadeApi } from './cascadeApi'
import type { CascadeTurnParams } from '../types/domain'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})

describe('cascadeApi', () => {
  it('postCascadeTurn POSTs multipart form-data with source and target always sent', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          turn: { turnId: 'turn_001', status: 'completed' },
          audioBase64: 'AAAA',
          audioContentType: 'audio/mpeg',
        }),
        {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const params: CascadeTurnParams = {
      sessionId: 'session_abc',
      turnId: 'turn_001',
      source: 'en',
      target: 'es',
      translationModel: 'gpt-5-nano',
      ttsVoice: 'alloy',
    }
    const audio = new Blob(['fake-audio-bytes'], { type: 'audio/webm' })

    const result = await cascadeApi.postCascadeTurn(params, audio)

    const [url, init] = fetchMock.mock.calls[0]
    const reqInit = init as RequestInit
    expect(url).toBe('/api/cascade/turn')
    expect(reqInit.method).toBe('POST')

    const form = reqInit.body as FormData
    expect(form).toBeInstanceOf(FormData)
    // Must NOT force a JSON (or any) content-type — fetch sets the multipart boundary itself.
    expect(new Headers(reqInit.headers).get('Content-Type')).toBeNull()
    expect(form.get('sessionId')).toBe('session_abc')
    expect(form.get('turnId')).toBe('turn_001')
    expect(form.get('source')).toBe('en') // always sent (mitigates the C.5 [Required] gap)
    expect(form.get('target')).toBe('es')
    expect(form.get('translationModel')).toBe('gpt-5-nano')
    expect(form.get('ttsVoice')).toBe('alloy')
    expect(form.get('audio')).toBeInstanceOf(Blob)

    expect(result.audioBase64).toBe('AAAA')
    expect(result.audioContentType).toBe('audio/mpeg')
  })

  it('postCascadeTurn omits turnId from the form when not provided', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ turn: { turnId: 'turn_002', status: 'completed' } }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const params: CascadeTurnParams = {
      sessionId: 'session_abc',
      source: 'es',
      target: 'en',
      translationModel: 'gpt-5-mini',
      ttsVoice: 'verse',
    }
    const audio = new Blob(['x'], { type: 'audio/webm' })

    await cascadeApi.postCascadeTurn(params, audio)

    const form = (fetchMock.mock.calls[0][1] as RequestInit).body as FormData
    expect(form.has('turnId')).toBe(false)
    expect(form.get('source')).toBe('es')
    expect(form.get('target')).toBe('en')
  })
})
