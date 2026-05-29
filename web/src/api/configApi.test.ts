import { afterEach, describe, expect, it, vi } from 'vitest'
import { configApi } from './configApi'
import type { ConfigResponse } from '../types/domain'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.unstubAllEnvs()
})

const config: ConfigResponse = {
  realtime: { configured: true, models: ['gpt-realtime', 'gpt-realtime-mini'] },
  cascade: {
    stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
    translation: { configured: true, provider: 'openai', models: ['gpt-5.4-nano', 'gpt-5.4-mini'] },
    tts: { configured: false, provider: 'openai', model: 'gpt-4o-mini-tts' },
  },
  languages: ['en', 'es'],
  pricingConfigVersion: '2026-05-28-payg-estimates',
}

describe('configApi', () => {
  it('GETs /api/config and returns the typed ConfigResponse', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(config), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await configApi.getConfig()

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/config')
    expect((init as RequestInit).method).toBe('GET')
    expect(result).toEqual(config)
  })
})
