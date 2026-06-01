import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSoakWer } from './soakWerClient'
import { ApiError } from '../api/http'

// The WER production wiring (089b / 090): the soak's `computeWer` seam → `POST /api/evaluation/wer`
// with the explicit-reference body `{ sessionId, reference, hypothesis }` (090's additive path — NO
// phraseId, NO turnId, so the soak turns stay in the comparison). The canonical WER algorithm is the
// backend's (no client-side calc). Request-shaping is fetch-mocked (web §4, the fetchDevTtsAudio precedent).
describe('createSoakWer', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('POSTs {sessionId, reference, hypothesis} to /api/evaluation/wer and returns result.wer', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      Promise.resolve(
        new Response(JSON.stringify({ result: { wer: 0.25 } }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const computeWer = createSoakWer('sess-1')
    const wer = await computeWer('the reference', 'the hypothesis')

    expect(wer).toBe(0.25)
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/evaluation/wer')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({
      sessionId: 'sess-1',
      reference: 'the reference',
      hypothesis: 'the hypothesis',
    })
  })

  it('surfaces a non-OK response as a sanitized ApiError (no client-side WER fallback)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('boom', { status: 500 })))
    const computeWer = createSoakWer('sess-1')
    await expect(computeWer('r', 'h')).rejects.toBeInstanceOf(ApiError)
  })
})
