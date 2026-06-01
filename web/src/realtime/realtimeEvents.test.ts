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
    // outputAudio←output_token_details.audio_tokens, cached←input_token_details.cached_tokens_details
    // .audio_tokens (095 — the cached AUDIO subset, NOT the aggregate cached_tokens which also counts text).
    // The fixture (runbook event #20): input audio 31, output audio 54, cached audio 0.
    const frame = {
      type: 'response.done',
      response: {
        status: 'completed',
        usage: {
          total_tokens: 139,
          input_tokens: 68,
          input_token_details: {
            text_tokens: 37,
            audio_tokens: 31,
            cached_tokens: 0,
            cached_tokens_details: { text_tokens: 0, audio_tokens: 0 },
          },
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

  it('reads cached audio from cached_tokens_details.audio_tokens, NOT the aggregate cached_tokens (095)', () => {
    // CF76 live shape: input_token_details carries BOTH cached_tokens (total = text+audio) AND
    // cached_tokens_details:{text_tokens, audio_tokens} (the modality breakdown). cachedAudioInputTokens must
    // carry the AUDIO subset (320), never the aggregate (512 = 192 text + 320 audio) — so the BE (paired
    // slice 094) discounts only the cached AUDIO at the cached-audio rate, not the text too. web §26.
    const frame = {
      type: 'response.done',
      response: {
        usage: {
          input_token_details: {
            audio_tokens: 498,
            cached_tokens: 512,
            cached_tokens_details: { text_tokens: 192, audio_tokens: 320 },
          },
          output_token_details: { audio_tokens: 71 },
        },
      },
    }
    expect(normalizeRealtimeEvent(frame)).toEqual({
      kind: 'responseDone',
      usage: { inputAudioTokens: 498, outputAudioTokens: 71, cachedAudioInputTokens: 320 },
    })
  })

  it('keeps a real cached_tokens_details.audio_tokens 0 (present) as 0 — real-0 ≠ absent (095, web §26)', () => {
    // The cached-audio subset present and genuinely 0 is forwarded as 0 (distinct from an absent breakdown,
    // which omits the field — the companion test below). Early-conversation turns carry cached audio 0.
    const frame = {
      type: 'response.done',
      response: {
        usage: {
          input_token_details: { audio_tokens: 31, cached_tokens_details: { audio_tokens: 0 } },
          output_token_details: { audio_tokens: 54 },
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
        input_token_details: {
          audio_tokens: 31,
          cached_tokens: 0,
          cached_tokens_details: { audio_tokens: 0 },
        },
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

  it('does NOT fall back to the aggregate cached_tokens when cached_tokens_details is absent → omitted (095)', () => {
    // The aggregate cached_tokens (text+audio) is present but the cached_tokens_details breakdown is absent.
    // The old code read the aggregate into cachedAudioInputTokens; 095 reads ONLY the audio subset, so with
    // no breakdown the field is OMITTED (absent ≠ 0; never the aggregate; web §23/§26). Pins the Q3 decision
    // to drop the cached_tokens read entirely.
    const frame = {
      type: 'response.done',
      response: {
        usage: {
          input_token_details: { audio_tokens: 31, cached_tokens: 50 },
          output_token_details: { audio_tokens: 54 },
        },
      },
    }
    expect(normalizeRealtimeEvent(frame)).toEqual({
      kind: 'responseDone',
      usage: { inputAudioTokens: 31, outputAudioTokens: 54 },
    })
  })

  it('maps the server-VAD buffer events (speech_started / speech_stopped / committed) — I.2 slice 2', () => {
    // Phase-I auto-VAD: under turn_detection:server_vad the server emits these per detected speech segment
    // (input_audio_buffer.speech_started → …speech_stopped → input_audio_buffer.committed → auto
    // response.created → response.done). Standard GA server events (Context7-confirmed family; the exact
    // type strings are re-pinned against the live oai-events capture at the next auto-VAD smoke, §27/§15).
    // They carry only ids/offsets (item_id / audio_start_ms / audio_end_ms) — none the controller needs —
    // so the normalized events are payload-less lifecycle markers the controller acts on (begin/anchor).
    expect(
      normalizeRealtimeEvent({
        type: 'input_audio_buffer.speech_started',
        audio_start_ms: 120,
        item_id: 'item_1',
      }),
    ).toEqual({ kind: 'speechStarted' })
    expect(
      normalizeRealtimeEvent({
        type: 'input_audio_buffer.speech_stopped',
        audio_end_ms: 880,
        item_id: 'item_1',
      }),
    ).toEqual({ kind: 'speechStopped' })
    expect(
      normalizeRealtimeEvent({
        type: 'input_audio_buffer.committed',
        item_id: 'item_1',
        previous_item_id: null,
      }),
    ).toEqual({ kind: 'committed' })
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
