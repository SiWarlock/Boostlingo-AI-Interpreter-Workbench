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
    // 053-C2b: responseDone now carries `usage` (null for a bare frame with no usage payload).
    expect(normalizeRealtimeEvent({ type: 'response.done' })).toEqual({
      kind: 'responseDone',
      usage: null,
    })
  })

  it('extracts exact audio-token usage from response.done (053-C2b — the fixture frame)', () => {
    // GA response.done nests usage under `response.usage`. inputAudio←input_token_details.audio_tokens,
    // outputAudio←output_token_details.audio_tokens, cached←input_token_details.cached_tokens (BE-confirmed
    // path, 053-C2a). The fixture (runbook event #20): input audio 31, output audio 54, cached 0.
    const frame = {
      type: 'response.done',
      response: {
        status: 'completed',
        usage: {
          total_tokens: 139,
          input_tokens: 68,
          input_token_details: { text_tokens: 37, audio_tokens: 31, cached_tokens: 0 },
          output_tokens: 71,
          output_token_details: { text_tokens: 17, audio_tokens: 54 },
        },
      },
    }
    expect(normalizeRealtimeEvent(frame)).toEqual({
      kind: 'responseDone',
      usage: { inputAudioTokens: 31, outputAudioTokens: 54, cachedAudioInputTokens: 0 },
    })
  })

  it('extracts usage from a TOP-LEVEL usage too (the runbook print-shape) — dual-read pins both nesting branches', () => {
    // The runbook trims fields + collapses the `response:` wrapper, printing usage flat. The dual-read
    // `e.response?.usage ?? e.usage` must extract identically whether usage is nested (GA wire, A1 above)
    // or top-level (runbook shape) — a wrong path ⇒ usage always null ⇒ realtime cost silently stays n/a.
    const flatFrame = {
      type: 'response.done',
      status: 'completed',
      usage: {
        input_token_details: { audio_tokens: 31, cached_tokens: 0 },
        output_token_details: { audio_tokens: 54 },
      },
    }
    expect(normalizeRealtimeEvent(flatFrame)).toEqual({
      kind: 'responseDone',
      usage: { inputAudioTokens: 31, outputAudioTokens: 54, cachedAudioInputTokens: 0 },
    })
  })

  it('response.done with absent/malformed usage → usage null (never throws)', () => {
    expect(normalizeRealtimeEvent({ type: 'response.done', response: {} })).toEqual({
      kind: 'responseDone',
      usage: null,
    })
    expect(
      normalizeRealtimeEvent({ type: 'response.done', response: { usage: 'not-an-object' } }),
    ).toEqual({ kind: 'responseDone', usage: null })
    // a non-object `response` (number / null) must also degrade cleanly via the asObject guard (§9)
    expect(normalizeRealtimeEvent({ type: 'response.done', response: 42 })).toEqual({
      kind: 'responseDone',
      usage: null,
    })
    expect(normalizeRealtimeEvent({ type: 'response.done', response: null })).toEqual({
      kind: 'responseDone',
      usage: null,
    })
  })

  it('guards each usage field independently — partial usage yields only the present fields', () => {
    // output_token_details present, input_token_details absent → only outputAudioTokens set (no fabricated 0s).
    const frame = {
      type: 'response.done',
      response: { usage: { output_token_details: { audio_tokens: 54 } } },
    }
    expect(normalizeRealtimeEvent(frame)).toEqual({
      kind: 'responseDone',
      usage: { outputAudioTokens: 54 },
    })
  })

  it('distinguishes a real cached 0 (present) from an absent cached field (omitted)', () => {
    // cached_tokens absent → cachedAudioInputTokens OMITTED (the honest-degrade pin; A1 covers cached=0 present).
    const frame = {
      type: 'response.done',
      response: {
        usage: {
          input_token_details: { audio_tokens: 31 },
          output_token_details: { audio_tokens: 54 },
        },
      },
    }
    expect(normalizeRealtimeEvent(frame)).toEqual({
      kind: 'responseDone',
      usage: { inputAudioTokens: 31, outputAudioTokens: 54 },
    })
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
