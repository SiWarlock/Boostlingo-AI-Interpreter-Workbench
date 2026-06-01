import { describe, expect, it } from 'vitest'
import {
  buildSoakScheduleFromBuffers,
  countTransportDisconnects,
  isTransportDisconnect,
} from './composeSoakDrive'
import type { TurnViewModel, UiError } from '../types/domain'

// `composeSoakDrive` is mostly SMOKE (real browser audio + real capture/realtime clients + the real drive)
// — validated at the manual real-key run. Its one deterministic, correctness-critical bit is index-aligning
// the decoded buffer durations to the 087 schedule (a wrong alignment desyncs the whole conversation). That
// helper is TDD'd here.
describe('buildSoakScheduleFromBuffers', () => {
  it('builds an index-aligned schedule from decoded buffer durations (seconds → ms) + the gap', () => {
    const buffers = [{ duration: 1.0 }, { duration: 1.5 }] as unknown as AudioBuffer[]

    const schedule = buildSoakScheduleFromBuffers(buffers, 500)

    // AudioBuffer.duration is SECONDS → ms; durations 1000/1500 + gap 500 → offsets 0, 1500.
    expect(schedule.utterances.map((u) => u.durationMs)).toEqual([1000, 1500])
    expect(schedule.utterances.map((u) => u.startOffsetMs)).toEqual([0, 1500])
  })
})

// The PRECISE transport-disconnect count (093 — replaces the 089b failed-turn proxy). Only the codes the
// WS-close / pc-`failed` paths emit count: cascade `cascade.connection_lost` (failIfLive on abnormal close)
// + realtime `realtime.session.disconnected` (the connection manager on pc disconnected/failed). A
// provider-error failure (e.g. stt.timeout) is NOT a transport disconnect — the proxy over-counted it.
function turnWithErrorCodes(...codes: string[]): TurnViewModel {
  const errors: UiError[] = codes.map((code) => ({ code, safeMessage: 'x', retryable: true }))
  return {
    turnId: 't',
    mode: 'cascade',
    direction: { source: 'en', target: 'es' },
    status: 'failed',
    startedAt: '2026-01-01T00:00:00.000Z',
    sourceTranscript: [],
    targetTranscript: [],
    latency: {},
    errors,
  }
}

describe('transport-disconnect counting', () => {
  it('classifies only the transport-close codes as disconnects', () => {
    expect(isTransportDisconnect('cascade.connection_lost')).toBe(true)
    expect(isTransportDisconnect('realtime.session.disconnected')).toBe(true)
    expect(isTransportDisconnect('stt.timeout')).toBe(false) // provider error, NOT a transport disconnect
  })

  it('counts ONLY transport-disconnect turns (precise; excludes provider-error failures + clean turns)', () => {
    const turns = [
      turnWithErrorCodes('cascade.connection_lost'),
      turnWithErrorCodes('realtime.session.disconnected'),
      turnWithErrorCodes('stt.timeout'), // the 089b failed-turn proxy WOULD have counted this — we don't
      turnWithErrorCodes(), // clean
    ]
    expect(countTransportDisconnects(turns)).toBe(2)
  })
})
