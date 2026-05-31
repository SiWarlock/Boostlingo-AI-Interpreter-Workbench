import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  buildStartFrame,
  createCascadeStreamClient,
  dispatchCascadeMessage,
  toWebSocketUrl,
  type CascadeStartParams,
} from './cascadeStreamClient'
import type { CostEstimate, LatencyEvent, ProviderError, TranscriptSegment } from '../types/domain'

afterEach(() => {
  vi.unstubAllEnvs()
})

const params: CascadeStartParams = {
  sessionId: 'session_abc',
  turnId: 'turn_001',
  direction: { source: 'en', target: 'es' },
  sampleRate: 48000,
  translationModel: 'gpt-5-nano',
  ttsVoice: 'alloy',
}

// Minimal fake WebSocket — the injected seam that makes the client lifecycle unit-testable.
class FakeWebSocket {
  binaryType = ''
  sent: Array<string | ArrayBufferLike> = []
  closed = false
  onopen: (() => void) | null = null
  onmessage: ((event: { data: unknown }) => void) | null = null
  onclose: ((event: { wasClean: boolean }) => void) | null = null
  onerror: (() => void) | null = null
  constructor(public url: string) {}
  send(data: string | ArrayBufferLike): void {
    this.sent.push(data)
  }
  close(): void {
    this.closed = true
  }
}

function makeMockStore() {
  return {
    appendTranscriptSegment: vi.fn(),
    appendLatencyEvent: vi.fn(),
    setTurnCost: vi.fn(),
    failTurn: vi.fn(),
    completeTurn: vi.fn(),
  }
}

const fakeLocation = { protocol: 'http:', host: 'localhost:5173' }

describe('toWebSocketUrl', () => {
  it('derives the ws(s) scheme from the base or the page location', () => {
    expect(toWebSocketUrl('', { protocol: 'https:', host: 'app.example.com' })).toBe(
      'wss://app.example.com/api/cascade/stream',
    )
    expect(toWebSocketUrl('', { protocol: 'http:', host: 'localhost:5173' })).toBe(
      'ws://localhost:5173/api/cascade/stream',
    )
    expect(toWebSocketUrl('http://localhost:5179', fakeLocation)).toBe(
      'ws://localhost:5179/api/cascade/stream',
    )
    expect(toWebSocketUrl('https://api.example.com', fakeLocation)).toBe(
      'wss://api.example.com/api/cascade/stream',
    )
    // a trailing slash on the base must not produce a double slash
    expect(toWebSocketUrl('http://localhost:5179/', fakeLocation)).toBe(
      'ws://localhost:5179/api/cascade/stream',
    )
  })
})

describe('buildStartFrame', () => {
  it('builds the exact ARCH-009 start frame', () => {
    expect(buildStartFrame(params)).toEqual({
      type: 'start',
      sessionId: 'session_abc',
      turnId: 'turn_001',
      direction: { source: 'en', target: 'es' },
      encoding: 'linear16',
      sampleRate: 48000,
      translationModel: 'gpt-5-nano',
      ttsVoice: 'alloy',
    })
  })

  it('includes autoVad only when truthy (auto); omits it when false/undefined (manual — wire unchanged) — I.3', () => {
    // Phase-I cascade auto-VAD (062 backend): the start frame carries `autoVad` so the backend
    // auto-finalizes on Deepgram utterance-end. Omit-when-falsy keeps the MANUAL frame byte-identical to
    // pre-062 (the exact-frame test above pins that — the manual key must be absent, not present-false).
    expect(buildStartFrame({ ...params, autoVad: true })).toMatchObject({ autoVad: true })
    expect('autoVad' in buildStartFrame({ ...params, autoVad: false })).toBe(false)
    expect('autoVad' in buildStartFrame(params)).toBe(false)
  })
})

describe('dispatchCascadeMessage', () => {
  const segment: TranscriptSegment = {
    segmentId: 's1',
    role: 'source',
    text: 'hola',
    isFinal: true,
    provider: 'deepgram',
    timestamp: '2026-05-29T12:00:00+00:00',
    clockSource: 'server',
  }
  const event: LatencyEvent = {
    name: 'stt.final',
    stage: 'stt',
    timestamp: '2026-05-29T12:00:00+00:00',
    relativeMs: 250,
    clockSource: 'server',
    metadata: {},
  }
  const estimate: CostEstimate = {
    provider: 'cascade',
    model: 'gpt-5-nano',
    pricingBasis: 'composite',
    estimatedUsd: 0.01,
    estimatedUsdPerMinute: 0.6,
    units: {},
    pricingConfigVersion: 'v1',
    assumptions: [],
  }
  const providerError: ProviderError = {
    provider: 'openai',
    stage: 'translation',
    code: 'translation.timeout',
    safeMessage: 'Translation timed out.',
    retryable: true,
  }

  it('routes each server message to the matching store action / onAudio callback', () => {
    const store = makeMockStore()
    const onAudio = vi.fn()

    dispatchCascadeMessage(JSON.stringify({ type: 'transcript', segment }), { store, onAudio })
    expect(store.appendTranscriptSegment).toHaveBeenCalledWith(segment)

    dispatchCascadeMessage(JSON.stringify({ type: 'latency', event }), { store, onAudio })
    expect(store.appendLatencyEvent).toHaveBeenCalledWith(event)

    dispatchCascadeMessage(JSON.stringify({ type: 'cost', estimate }), { store, onAudio })
    expect(store.setTurnCost).toHaveBeenCalledWith(estimate)

    dispatchCascadeMessage(JSON.stringify({ type: 'error', error: providerError }), {
      store,
      onAudio,
    })
    // ProviderError is projected to a UiError for failTurn (drops provider/httpStatusCode).
    expect(store.failTurn).toHaveBeenCalledWith({
      code: 'translation.timeout',
      safeMessage: 'Translation timed out.',
      stage: 'translation',
      retryable: true,
    })

    dispatchCascadeMessage(
      JSON.stringify({ type: 'done', turnId: 'turn_001', status: 'completed' }),
      {
        store,
        onAudio,
      },
    )
    expect(store.completeTurn).toHaveBeenCalledWith('turn_001', 'completed')
  })

  it('routes audio to onAudio and NEVER to the store (raw audio off the store, invariant #3)', () => {
    const store = makeMockStore()
    const onAudio = vi.fn()

    dispatchCascadeMessage(
      JSON.stringify({ type: 'audio', contentType: 'audio/mpeg', seq: 0, base64: 'AAAA' }),
      { store, onAudio },
    )

    expect(onAudio).toHaveBeenCalledWith({ contentType: 'audio/mpeg', seq: 0, base64: 'AAAA' })
    expect(store.appendTranscriptSegment).not.toHaveBeenCalled()
    expect(store.appendLatencyEvent).not.toHaveBeenCalled()
    expect(store.setTurnCost).not.toHaveBeenCalled()
    expect(store.failTurn).not.toHaveBeenCalled()
    expect(store.completeTurn).not.toHaveBeenCalled()
  })

  it('ignores malformed JSON and unknown message types without throwing or mutating', () => {
    const store = makeMockStore()
    const onAudio = vi.fn()

    expect(() => dispatchCascadeMessage('not json{', { store, onAudio })).not.toThrow()
    expect(() =>
      dispatchCascadeMessage(JSON.stringify({ type: 'mystery' }), { store, onAudio }),
    ).not.toThrow()
    expect(() =>
      dispatchCascadeMessage(JSON.stringify({ no: 'type' }), { store, onAudio }),
    ).not.toThrow()
    // A known type with a missing sub-field must NOT throw past the router (would stall the turn).
    expect(() =>
      dispatchCascadeMessage(JSON.stringify({ type: 'error' }), { store, onAudio }),
    ).not.toThrow()

    expect(onAudio).not.toHaveBeenCalled()
    expect(store.appendTranscriptSegment).not.toHaveBeenCalled()
    expect(store.failTurn).not.toHaveBeenCalled()
    expect(store.completeTurn).not.toHaveBeenCalled()
  })
})

describe('cascadeStreamClient lifecycle', () => {
  function setup() {
    const fakes: FakeWebSocket[] = []
    const wsFactory = (url: string): WebSocket => {
      const fake = new FakeWebSocket(url)
      fakes.push(fake)
      return fake as unknown as WebSocket
    }
    const store = makeMockStore()
    const onAudio = vi.fn()
    const onTerminal = vi.fn()
    const client = createCascadeStreamClient({
      store,
      onAudio,
      onTerminal,
      wsFactory,
      location: fakeLocation,
    })
    return { client, store, onAudio, onTerminal, fakes, getFake: () => fakes[fakes.length - 1] }
  }

  it('opens with arraybuffer binaryType and sends the start frame on open', () => {
    const { client, getFake } = setup()
    client.start(params)
    const fake = getFake()
    expect(fake.binaryType).toBe('arraybuffer')
    expect(fake.sent).toHaveLength(0) // nothing sent before the socket opens

    fake.onopen?.()
    expect(fake.sent).toHaveLength(1)
    expect(JSON.parse(fake.sent[0] as string)).toEqual(buildStartFrame(params))
  })

  it('queues frames sent before open and flushes them after the start frame (D.4b race)', () => {
    const { client, getFake } = setup()
    client.start(params)
    const fake = getFake()

    // The socket is CONNECTING — capture frames begin flowing before open. They must NOT be sent on
    // a not-open socket, and must NOT be dropped.
    const buf = new ArrayBuffer(4)
    client.sendFrame(buf)
    expect(fake.sent).toHaveLength(0)

    fake.onopen?.()
    // on open: the start frame first, then the queued PCM frame, in order
    expect(fake.sent).toHaveLength(2)
    expect(JSON.parse(fake.sent[0] as string)).toEqual(buildStartFrame(params))
    expect(fake.sent[1]).toBe(buf)
  })

  it('forwards binary frames and sends the stop frame', () => {
    const { client, getFake } = setup()
    client.start(params)
    const fake = getFake()
    fake.onopen?.()

    const buf = new ArrayBuffer(8)
    client.sendFrame(buf)
    expect(fake.sent).toContain(buf) // binary frame forwarded verbatim

    client.stop()
    expect(fake.sent[fake.sent.length - 1]).toBe(JSON.stringify({ type: 'stop' }))
  })

  it('dispatches inbound text frames into the store', () => {
    const { client, store, getFake } = setup()
    client.start(params)
    const fake = getFake()
    fake.onopen?.()

    fake.onmessage?.({
      data: JSON.stringify({ type: 'done', turnId: 'turn_001', status: 'completed' }),
    })
    expect(store.completeTurn).toHaveBeenCalledWith('turn_001', 'completed')
  })

  it('fails the turn on an abnormal close/error before done, but not after done', () => {
    // Abnormal close before done -> connection_lost.
    const a = setup()
    a.client.start(params)
    a.getFake().onopen?.()
    a.getFake().onclose?.({ wasClean: false })
    expect(a.store.failTurn).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'cascade.connection_lost' }),
    )

    // onerror before done -> connection_lost.
    const b = setup()
    b.client.start(params)
    b.getFake().onopen?.()
    b.getFake().onerror?.()
    expect(b.store.failTurn).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'cascade.connection_lost' }),
    )

    // Close AFTER done -> no error (normal teardown).
    const c = setup()
    c.client.start(params)
    c.getFake().onopen?.()
    c.getFake().onmessage?.({
      data: JSON.stringify({ type: 'done', turnId: 'turn_001', status: 'completed' }),
    })
    c.getFake().onclose?.({ wasClean: true })
    expect(c.store.failTurn).not.toHaveBeenCalled()
  })

  it('treats a server error frame as terminal — no spurious connection_lost on the following close', () => {
    const { client, store, onTerminal, getFake } = setup()
    client.start(params)
    getFake().onopen?.()
    getFake().onmessage?.({
      data: JSON.stringify({
        type: 'error',
        error: {
          provider: 'openai',
          stage: 'translation',
          code: 'translation.timeout',
          safeMessage: 'x',
          retryable: true,
        },
      }),
    })
    getFake().onclose?.({ wasClean: false }) // server closes after the error frame

    // exactly one failTurn — the real provider error, NOT a second cascade.connection_lost
    expect(store.failTurn).toHaveBeenCalledTimes(1)
    expect(store.failTurn).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'translation.timeout' }),
    )
    // the `terminal` latch also suppresses a 2nd capture-stop on the error-then-close path (I.3)
    expect(onTerminal).toHaveBeenCalledTimes(1)
  })

  it('tears down the previous socket on a second start so its close cannot fail the new turn', () => {
    const { client, store, fakes } = setup()
    client.start(params)
    fakes[0].onopen?.()

    client.start({ ...params, turnId: 'turn_002' }) // a second turn
    expect(fakes).toHaveLength(2)
    expect(fakes[0].closed).toBe(true) // old socket closed
    expect(fakes[0].onclose).toBeNull() // old handlers detached

    // the (already-detached) old socket closing must NOT fail the new turn
    fakes[0].onclose?.({ wasClean: false })
    expect(store.failTurn).not.toHaveBeenCalled()
  })

  it('defers the stop frame until open when stopped before the socket opens', () => {
    const { client, getFake } = setup()
    client.start(params)
    const fake = getFake()

    // Stop clicked while CONNECTING — must NOT throw / send on a not-open socket.
    expect(() => client.stop()).not.toThrow()
    expect(fake.sent).toHaveLength(0)

    fake.onopen?.()
    // on open: the start frame, then the deferred stop
    expect(fake.sent).toHaveLength(2)
    expect(JSON.parse(fake.sent[0] as string)).toEqual(buildStartFrame(params))
    expect(fake.sent[1]).toBe(JSON.stringify({ type: 'stop' }))
  })

  it('sends exactly one stop frame on a double stop (idempotent)', () => {
    const { client, getFake } = setup()
    client.start(params)
    const fake = getFake()
    fake.onopen?.()

    client.stop()
    client.stop()

    const stops = fake.sent.filter((s) => s === JSON.stringify({ type: 'stop' }))
    expect(stops).toHaveLength(1)
  })

  // --- I.3: the capture-stop hook on a terminal -------------------------------------------------
  // In auto mode no frontend Stop fires (the backend auto-finalizes on Deepgram utterance-end), so the
  // audio capture would keep running. The client invokes onTerminal on every turn-end path so
  // recordingActions can stop the mic (mirrors the onAudio delegate; idempotent with a manual Stop).
  it('invokes onTerminal on a done frame (the auto-finalize capture-stop hook)', () => {
    const { client, onTerminal, getFake } = setup()
    client.start(params)
    getFake().onopen?.()
    getFake().onmessage?.({
      data: JSON.stringify({ type: 'done', turnId: 'turn_001', status: 'completed' }),
    })
    expect(onTerminal).toHaveBeenCalledTimes(1)
  })

  it('invokes onTerminal on the other turn-end paths too (error frame, abnormal close) — no mic leak on any auto turn-end', () => {
    const a = setup()
    a.client.start(params)
    a.getFake().onopen?.()
    a.getFake().onmessage?.({
      data: JSON.stringify({
        type: 'error',
        error: {
          provider: 'openai',
          stage: 'translation',
          code: 'translation.timeout',
          safeMessage: 'x',
          retryable: true,
        },
      }),
    })
    expect(a.onTerminal).toHaveBeenCalledTimes(1)

    const b = setup()
    b.client.start(params)
    b.getFake().onopen?.()
    b.getFake().onclose?.({ wasClean: false }) // abnormal disconnect before any terminal frame
    expect(b.onTerminal).toHaveBeenCalledTimes(1)
  })

  it('does NOT invoke onTerminal on a non-terminal frame (transcript)', () => {
    const { client, onTerminal, getFake } = setup()
    client.start(params)
    getFake().onopen?.()
    getFake().onmessage?.({
      data: JSON.stringify({
        type: 'transcript',
        segment: {
          segmentId: 's',
          role: 'source',
          text: 'hola',
          isFinal: false,
          provider: 'deepgram',
          timestamp: '2026-05-29T12:00:00+00:00',
          clockSource: 'server',
        },
      }),
    })
    expect(onTerminal).not.toHaveBeenCalled()
  })

  it('fires onTerminal exactly once across done-then-close (the terminal guard — no double capture-stop)', () => {
    const { client, onTerminal, getFake } = setup()
    client.start(params)
    getFake().onopen?.()
    getFake().onmessage?.({
      data: JSON.stringify({ type: 'done', turnId: 'turn_001', status: 'completed' }),
    })
    getFake().onclose?.({ wasClean: true }) // normal post-done close must NOT re-fire onTerminal
    expect(onTerminal).toHaveBeenCalledTimes(1)
  })
})
