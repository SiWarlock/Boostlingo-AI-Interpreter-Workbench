import { describe, expect, it } from 'vitest'
import { createRealtimeEventSink } from './realtimeEventSink'
import { createSessionStore } from '../state/sessionStore'
import type { SessionStore } from '../state/sessionStore'

const FIXED_TS = '2026-05-29T12:00:00.000+00:00'
const fixedClock = (): string => FIXED_TS

// A real store with an active realtime turn — the sink's collaborator (the streaming actions are
// mode-agnostic, reused from cascade). Tested against the real store (lesson §7) so the state sentinel +
// the cumulative-render + no-double-stamp assertions are genuine.
function setupTurn(): SessionStore {
  const store = createSessionStore()
  store.beginTurn({ turnId: 'turn_1', mode: 'realtime', direction: { source: 'en', target: 'es' } })
  return store
}

describe('createRealtimeEventSink', () => {
  it('appends a non-final target segment on targetTranscriptDelta', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'targetTranscriptDelta', text: 'hola' })

    expect(store.getState().currentTurn?.targetTranscript).toEqual([
      { text: 'hola', isFinal: false },
    ])
  })

  it('appends a non-final source segment on sourceTranscriptDelta', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'sourceTranscriptDelta', text: 'hel' })

    expect(store.getState().currentTurn?.sourceTranscript).toEqual([
      { text: 'hel', isFinal: false },
    ])
  })

  it('appends a FINAL source segment on sourceTranscriptCompleted', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'sourceTranscriptCompleted', text: 'hello' })

    expect(store.getState().currentTurn?.sourceTranscript).toEqual([
      { text: 'hello', isFinal: true },
    ])
  })

  it('replaces accumulated source partials with the authoritative final on sourceTranscriptCompleted', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'sourceTranscriptDelta', text: 'hel' })
    sink.handle({ kind: 'sourceTranscriptDelta', text: 'lo' })
    sink.handle({ kind: 'sourceTranscriptCompleted', text: 'hello' })

    // The running cumulative partial ('hello') is replaced by the authoritative final segment; a later
    // stray delta would start a fresh partial (accumulator reset), not concatenate onto stale text.
    expect(store.getState().currentTurn?.sourceTranscript).toEqual([
      { text: 'hello', isFinal: true },
    ])
  })

  it('accumulates incremental target tokens into one cumulative partial + stamps first_transcript_delta once', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'targetTranscriptDelta', text: 'ho' })
    sink.handle({ kind: 'targetTranscriptDelta', text: 'la' })

    // The store's appendSegment (§10) REPLACES the trailing non-final partial (cascade-cumulative model),
    // so the sink passes CUMULATIVE text — the panel shows the growing transcript, not just the last token.
    expect(store.getState().currentTurn?.targetTranscript).toEqual([
      { text: 'hola', isFinal: false },
    ])
    const events = store.getState().currentTurn?.latencyEvents ?? []
    expect(events.filter((e) => e.name === 'realtime.first_transcript_delta')).toHaveLength(1)
  })

  it('stamps realtime.first_audio_delta exactly once (browser clock) across two audioDeltas', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'audioDelta', base64: 'AAAA' })
    sink.handle({ kind: 'audioDelta', base64: 'BBBB' })

    const audioStamps = (store.getState().currentTurn?.latencyEvents ?? []).filter(
      (e) => e.name === 'realtime.first_audio_delta',
    )
    expect(audioStamps).toHaveLength(1)
    expect(audioStamps[0]).toMatchObject({
      stage: 'realtime',
      clockSource: 'browser',
      timestamp: FIXED_TS,
    })
  })

  it('stamps a per-turn playback.started on the first audioDelta (A2 — never a session-<audio> once-leak)', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'audioDelta', base64: 'AAAA' })
    sink.handle({ kind: 'audioDelta', base64: 'BBBB' })

    // A2: realtime playback timing is per-turn (stamped here, post-stop), not the session-persistent
    // <audio> onplaying once-latch that leaked a prior turn's stamp across turns (the negative-latency bug).
    const playbackStamps = (store.getState().currentTurn?.latencyEvents ?? []).filter(
      (e) => e.name === 'playback.started',
    )
    expect(playbackStamps).toHaveLength(1) // once per turn, on first audio
    expect(playbackStamps[0]).toMatchObject({
      stage: 'playback',
      clockSource: 'browser',
      timestamp: FIXED_TS,
    })
  })

  it('stamps first-audio (realtime.first_audio_delta + playback.started) once on outputAudioStarted (053-C1)', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'outputAudioStarted' })

    const events = store.getState().currentTurn?.latencyEvents ?? []
    expect(events.filter((e) => e.name === 'realtime.first_audio_delta')).toHaveLength(1)
    expect(events.filter((e) => e.name === 'playback.started')).toHaveLength(1)
  })

  it('latch: outputAudioStarted then audioDelta stamps first-audio exactly once (no double-stamp)', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'outputAudioStarted' })
    sink.handle({ kind: 'audioDelta', base64: 'AAAA' }) // fallback path — latch already held

    const events = store.getState().currentTurn?.latencyEvents ?? []
    expect(events.filter((e) => e.name === 'realtime.first_audio_delta')).toHaveLength(1)
    expect(events.filter((e) => e.name === 'playback.started')).toHaveLength(1)
  })

  it('latch (reverse): audioDelta then outputAudioStarted stamps first-audio exactly once', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'audioDelta', base64: 'AAAA' }) // whichever fires first wins via the shared latch
    sink.handle({ kind: 'outputAudioStarted' })

    const events = store.getState().currentTurn?.latencyEvents ?? []
    expect(events.filter((e) => e.name === 'realtime.first_audio_delta')).toHaveLength(1)
    expect(events.filter((e) => e.name === 'playback.started')).toHaveLength(1)
  })

  it('NEVER writes audio or a transcript to the store on audioDelta (invariant #3 sentinel)', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'audioDelta', base64: 'PLANTED_AUDIO_PAYLOAD_B64' })

    const turn = store.getState().currentTurn
    expect(turn?.sourceTranscript).toEqual([])
    expect(turn?.targetTranscript).toEqual([])
    // Strong sentinel: the base64 audio payload appears NOWHERE in the serialized store state.
    expect(JSON.stringify(store.getState())).not.toContain('PLANTED_AUDIO_PAYLOAD_B64')
  })

  it('completes the turn on responseDone with a single browser-clock turn.completed stamp (no empty target)', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'responseDone', usage: null }) // null usage → no output-token surface (present-usage surface tested below)

    const state = store.getState()
    expect(state.currentTurn).toBeUndefined()
    expect(state.turns).toHaveLength(1)
    expect(state.turns[0]).toMatchObject({ turnId: 'turn_1', status: 'completed' })
    // The store's completeTurn (D.6) OWNS the turn.completed stamp — the sink must NOT double-stamp.
    const completedStamps = (state.turns[0].latencyEvents ?? []).filter(
      (e) => e.name === 'turn.completed',
    )
    expect(completedStamps).toHaveLength(1)
    // No target deltas this turn → no empty final target segment emitted.
    expect(state.turns[0].targetTranscript).toEqual([])
  })

  it('surfaces the output-audio-token count onto the completed turn from responseDone.usage (the 093 seam)', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({
      kind: 'responseDone',
      usage: { inputAudioTokens: 85, outputAudioTokens: 73, cachedAudioInputTokens: 0 },
    })

    // 093 derives the realtime output-audio duration from the per-turn output-token count.
    expect(store.getState().turns[0].outputAudioTokens).toBe(73)
  })

  it('omits the output-audio-token count when responseDone carries no usage (honest-degrade)', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'responseDone', usage: null })

    expect(store.getState().turns[0].outputAudioTokens).toBeUndefined()
  })

  it('finalizes the running target partial on responseDone (single final segment) before completing', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'targetTranscriptDelta', text: 'ho' })
    sink.handle({ kind: 'targetTranscriptDelta', text: 'la' })
    sink.handle({ kind: 'responseDone', usage: null }) // null usage → no output-token surface (present-usage surface tested below)

    // response.done is the target-final signal (E.3 handoff): the accumulated target rides into turns[]
    // as a SINGLE final segment (finalize-target THEN completeTurn).
    const finalized = store.getState().turns[0]
    expect(finalized.targetTranscript).toEqual([{ text: 'hola', isFinal: true }])
    expect(finalized.status).toBe('completed')
  })

  it('fails the turn on error with a sanitized UiError (stage realtime, generic message, no raw leak)', () => {
    const store = setupTurn()
    const sink = createRealtimeEventSink({ store, clock: fixedClock })

    sink.handle({ kind: 'error', code: 'server_error' })

    const state = store.getState()
    expect(state.turnStatus).toBe('failed')
    const err = state.errors[state.errors.length - 1]
    expect(err).toMatchObject({ code: 'server_error', stage: 'realtime', retryable: false })
    // safeMessage is a fixed generic — never derived from the GA code/payload (E.3 already dropped the raw msg).
    expect(err.safeMessage).not.toContain('server_error')
  })
})
