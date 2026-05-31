import { describe, expect, it } from 'vitest'
import { normalizeRealtimeEvent, parseRealtimeEvent } from './realtimeEvents'

describe('parseRealtimeEvent', () => {
  it('returns null for malformed JSON (never throws)', () => {
    expect(parseRealtimeEvent('{not json')).toBeNull()
  })

  it('parses a well-formed JSON object', () => {
    expect(parseRealtimeEvent('{"type":"response.created"}')).toEqual({ type: 'response.created' })
  })

  it('returns null when the parsed JSON is not an object (e.g. a bare string/number)', () => {
    expect(parseRealtimeEvent('"just a string"')).toBeNull()
    expect(parseRealtimeEvent('42')).toBeNull()
  })
})

describe('normalizeRealtimeEvent', () => {
  it('maps response.output_audio.delta -> audioDelta', () => {
    expect(
      normalizeRealtimeEvent({ type: 'response.output_audio.delta', delta: 'YmFzZTY0' }),
    ).toEqual({
      kind: 'audioDelta',
      base64: 'YmFzZTY0',
    })
  })

  it('accepts the legacy response.audio.delta alias -> audioDelta', () => {
    expect(normalizeRealtimeEvent({ type: 'response.audio.delta', delta: 'YmFzZTY0' })).toEqual({
      kind: 'audioDelta',
      base64: 'YmFzZTY0',
    })
  })

  it('maps response.output_audio_transcript.delta -> targetTranscriptDelta', () => {
    expect(
      normalizeRealtimeEvent({ type: 'response.output_audio_transcript.delta', delta: 'hola' }),
    ).toEqual({ kind: 'targetTranscriptDelta', text: 'hola' })
  })

  it('maps input_audio_transcription delta and completed -> source transcript events', () => {
    expect(
      normalizeRealtimeEvent({
        type: 'conversation.item.input_audio_transcription.delta',
        delta: 'hel',
      }),
    ).toEqual({ kind: 'sourceTranscriptDelta', text: 'hel' })
    expect(
      normalizeRealtimeEvent({
        type: 'conversation.item.input_audio_transcription.completed',
        transcript: 'hello',
      }),
    ).toEqual({ kind: 'sourceTranscriptCompleted', text: 'hello' })
  })

  it('maps response lifecycle events (created/done)', () => {
    expect(normalizeRealtimeEvent({ type: 'response.created' })).toEqual({
      kind: 'responseCreated',
    })
    expect(normalizeRealtimeEvent({ type: 'response.done' })).toEqual({ kind: 'responseDone' })
  })

  it('maps output_audio_buffer.started -> outputAudioStarted (the DC first-audio anchor under WebRTC, 053-C1)', () => {
    // response.output_audio.delta never arrives on the DC (audio rides the media track);
    // output_audio_buffer.started DOES fire (fixture #13) → the real first-audio anchor. It carries only
    // response_id/event_id (no payload the sink needs), so the normalized event has no fields.
    expect(
      normalizeRealtimeEvent({ type: 'output_audio_buffer.started', response_id: 'resp_x' }),
    ).toEqual({ kind: 'outputAudioStarted' })
  })

  it('classifies error / response.error -> error carrying a safe code (never the raw message)', () => {
    expect(
      normalizeRealtimeEvent({
        type: 'error',
        error: { code: 'invalid_request_error', message: 'raw provider detail' },
      }),
    ).toEqual({ kind: 'error', code: 'invalid_request_error' })
    expect(normalizeRealtimeEvent({ type: 'response.error', error: {} })).toEqual({
      kind: 'error',
      code: 'realtime.error',
    })
  })

  it('returns null for an unknown event type', () => {
    expect(normalizeRealtimeEvent({ type: 'some.unknown.event', foo: 1 })).toBeNull()
  })

  it('returns null for a malformed event (non-object, missing type, blank/absent payload field)', () => {
    expect(normalizeRealtimeEvent(null)).toBeNull()
    expect(normalizeRealtimeEvent({})).toBeNull()
    expect(normalizeRealtimeEvent({ type: 'response.output_audio.delta' })).toBeNull()
    // An empty-string payload is treated as absent by the nonEmptyString guard (not a 0-length delta).
    expect(normalizeRealtimeEvent({ type: 'response.output_audio.delta', delta: '' })).toBeNull()
  })
})
