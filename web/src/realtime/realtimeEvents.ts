// Pure, stateless GA Realtime event normalizer (ARCH-010 §7 — "GA event mapping"). The realtime analogue
// of the cascade dispatch router (web lesson §9): `parseRealtimeEvent` guards the JSON, `normalizeRealtimeEvent`
// classifies a GA `oai-events` data-channel event into a semantic NormalizedRealtimeEvent. NO state, NO store,
// NO clock — E.4 owns the stateful first-event latency stamping + LatencyEvent construction + store dispatch
// (mirrors cascade §10 store actions). Unknown / malformed events return null (ignored, never throw) — the
// guard-the-body discipline (lesson §9). The exact GA `type` strings + error envelope are smoke-confirmable
// (ARCH-010 §7 note); classification is stable regardless of envelope detail.

export type NormalizedRealtimeEvent =
  | { kind: 'audioDelta'; base64: string }
  | { kind: 'outputAudioStarted' }
  | { kind: 'targetTranscriptDelta'; text: string }
  | { kind: 'sourceTranscriptDelta'; text: string }
  | { kind: 'sourceTranscriptCompleted'; text: string }
  | { kind: 'responseCreated' }
  | { kind: 'responseDone' }
  | { kind: 'error'; code: string }

// Parse a raw data-channel text frame to a JSON object, or null when it is not valid JSON / not an object
// (so normalize only ever sees an object). Never throws.
export function parseRealtimeEvent(rawText: string): Record<string, unknown> | null {
  let parsed: unknown
  try {
    parsed = JSON.parse(rawText)
  } catch {
    return null
  }
  if (typeof parsed !== 'object' || parsed === null) {
    return null
  }
  return parsed as Record<string, unknown>
}

function nonEmptyString(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null
}

// Classify a parsed GA event. Returns the semantic NormalizedRealtimeEvent, or null for an unknown type /
// a known type missing its payload field (guarded like parse above).
export function normalizeRealtimeEvent(event: unknown): NormalizedRealtimeEvent | null {
  if (typeof event !== 'object' || event === null) {
    return null
  }
  const e = event as Record<string, unknown>

  switch (e.type) {
    // First arrival → E.4 stamps realtime.first_audio_delta. Accept the legacy `response.audio.delta` alias
    // (ARCH-010 §7 explicit legacy alias; dual-shape discipline, E.1 precedent).
    case 'response.output_audio.delta':
    case 'response.audio.delta': {
      const base64 = nonEmptyString(e.delta)
      return base64 ? { kind: 'audioDelta', base64 } : null
    }
    // Target (translated) transcript token (ARCH-010 §7).
    case 'response.output_audio_transcript.delta': {
      const text = nonEmptyString(e.delta)
      return text ? { kind: 'targetTranscriptDelta', text } : null
    }
    // Source transcript (requires input transcription enabled; "source unavailable" handling is E.4's render
    // concern). The delta carries `delta`; the completed event carries the full `transcript`.
    case 'conversation.item.input_audio_transcription.delta': {
      const text = nonEmptyString(e.delta)
      return text ? { kind: 'sourceTranscriptDelta', text } : null
    }
    case 'conversation.item.input_audio_transcription.completed': {
      const text = nonEmptyString(e.transcript)
      return text ? { kind: 'sourceTranscriptCompleted', text } : null
    }
    // Response lifecycle (ARCH-010 §7). E.4 stamps turn.completed on `response.done` + finalizes the target
    // transcript there.
    case 'response.created':
      return { kind: 'responseCreated' }
    case 'response.done':
      return { kind: 'responseDone' }
    // First-audio anchor under WebRTC (053-C1): output_audio_buffer.started is the DC event that actually
    // fires — response.output_audio.delta does NOT arrive (audio rides the media track, pc.ontrack). No
    // payload needed (carries only response_id/event_id); E.4's sink stamps the first-audio markers on it.
    // Confirmed GA `type` (ARCH-010 §7 audio-marker smoke-confirm).
    case 'output_audio_buffer.started':
      return { kind: 'outputAudioStarted' }
    // Surface (never swallow) a session error (ARCH-018). E.3 only CLASSIFIES — it carries the bounded GA
    // error code (a safe enum) and deliberately drops the raw `error.message`; the full ProviderError +
    // safeMessage construction is E.5's job.
    case 'error':
    case 'response.error': {
      const errorObject =
        typeof e.error === 'object' && e.error !== null ? (e.error as Record<string, unknown>) : {}
      const code = nonEmptyString(errorObject.code) ?? 'realtime.error'
      return { kind: 'error', code }
    }
    default:
      return null
  }
}
