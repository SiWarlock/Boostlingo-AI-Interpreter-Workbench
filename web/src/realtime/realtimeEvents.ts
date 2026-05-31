// Pure, stateless GA Realtime event normalizer (ARCH-010 §7 — "GA event mapping"). The realtime analogue
// of the cascade dispatch router (web lesson §9): `parseRealtimeEvent` guards the JSON, `normalizeRealtimeEvent`
// classifies a GA `oai-events` data-channel event into a semantic NormalizedRealtimeEvent. NO state, NO store,
// NO clock — E.4 owns the stateful first-event latency stamping + LatencyEvent construction + store dispatch
// (mirrors cascade §10 store actions). Unknown / malformed events return null (ignored, never throw) — the
// guard-the-body discipline (lesson §9). The exact GA `type` strings + error envelope are smoke-confirmable
// (ARCH-010 §7 note); classification is stable regardless of envelope detail.

// The exact realtime audio-token counts the FE forwards to /complete (053-C2b) — mapped from the DC
// response.done.usage. Each field is OPTIONAL + independently guarded; a real `cachedAudioInputTokens: 0`
// is distinct from absent (omitted) — never fabricate a 0 (web §25). Frontend-internal (not a wire mirror).
export type RealtimeUsageTokens = {
  inputAudioTokens?: number
  outputAudioTokens?: number
  cachedAudioInputTokens?: number
}

export type NormalizedRealtimeEvent =
  | { kind: 'audioDelta'; base64: string }
  | { kind: 'outputAudioStarted' }
  | { kind: 'targetTranscriptDelta'; text: string }
  | { kind: 'sourceTranscriptDelta'; text: string }
  | { kind: 'sourceTranscriptCompleted'; text: string }
  | { kind: 'responseCreated' }
  | { kind: 'responseDone'; usage: RealtimeUsageTokens | null }
  | { kind: 'error'; code: string }
  // Phase-I auto-VAD server-VAD buffer lifecycle (I.2 slice 2). Under turn_detection:server_vad the server
  // auto-detects each speech segment: speechStarted (new-segment marker → the controller begins a turn) →
  // speechStopped (the auto-mode speech-end anchor → the controller stamps turn.recording.stopped) →
  // committed (buffer auto-committed → the auto response.created/.done follow). Payload-less — they carry
  // only ids/offsets the controller doesn't need (item_id / audio_*_ms). Sequential under server-VAD.
  | { kind: 'speechStarted' }
  | { kind: 'speechStopped' }
  | { kind: 'committed' }

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

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

// A nullable-narrowing variant of isObject — returns the object or null, so an absent/non-object key reads
// cleanly via optional chaining (`asObject(x)?.field`) without the implicit CFA narrowing of a raw guard.
function asObject(value: unknown): Record<string, unknown> | null {
  return isObject(value) ? value : null
}

function finiteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

// Extract the exact realtime audio-token counts from a response.done frame's `usage` (053-C2b). GA nests
// usage under `response.usage`; tolerate a top-level `usage` too (dual-read — the wire shape is the
// load-bearing call: a wrong path ⇒ usage always null ⇒ realtime cost silently stays n/a). Guard every
// path independently (each may be absent/non-number); cached=0 is a REAL value (kept) vs absent (omitted).
// Returns null when usage is absent/malformed OR yields no token field (→ the controller still finalizes
// /complete, just without token fields — the honest-degrade path, web §25). Never throws (§9).
function extractRealtimeUsage(event: Record<string, unknown>): RealtimeUsageTokens | null {
  // GA nests usage under response.usage; tolerate a top-level usage too (dual-read — see the doc comment).
  const usage = asObject(asObject(event.response)?.usage) ?? asObject(event.usage)
  if (usage === null) {
    return null
  }
  const inputDetails = asObject(usage.input_token_details)
  const outputDetails = asObject(usage.output_token_details)
  const tokens: RealtimeUsageTokens = {}
  const inputAudio = finiteNumber(inputDetails?.audio_tokens)
  const outputAudio = finiteNumber(outputDetails?.audio_tokens)
  // Q5 (053-C2b): the BE-confirmed path is input_token_details.cached_tokens (the contract 2977f7f priced
  // from) — FE/BE must agree on one path. The audio-specific cached_tokens_details.audio_tokens nuance is a
  // Step-9 note (immaterial while cached=0).
  const cached = finiteNumber(inputDetails?.cached_tokens)
  if (inputAudio !== undefined) tokens.inputAudioTokens = inputAudio
  if (outputAudio !== undefined) tokens.outputAudioTokens = outputAudio
  if (cached !== undefined) tokens.cachedAudioInputTokens = cached
  return Object.keys(tokens).length > 0 ? tokens : null
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
      // E.4 stamps turn.completed + finalizes the target transcript; 053-C2b extracts the exact
      // audio-token usage the controller forwards to /complete for the realtime cost estimate.
      return { kind: 'responseDone', usage: extractRealtimeUsage(e) }
    // First-audio anchor under WebRTC (053-C1): output_audio_buffer.started is the DC event that actually
    // fires — response.output_audio.delta does NOT arrive (audio rides the media track, pc.ontrack). No
    // payload needed (carries only response_id/event_id); E.4's sink stamps the first-audio markers on it.
    // Confirmed GA `type` (ARCH-010 §7 audio-marker smoke-confirm).
    case 'output_audio_buffer.started':
      return { kind: 'outputAudioStarted' }
    // Phase-I server-VAD buffer lifecycle (I.2 slice 2). Standard GA server events under
    // turn_detection:server_vad — the exact type strings are re-pinned against the live oai-events capture
    // at the next auto-VAD smoke (ARCH-010 §7; §15/§27 verify-the-live-GA-shape). They carry only
    // ids/offsets (item_id / audio_*_ms) — none the controller needs — so they normalize payload-less.
    case 'input_audio_buffer.speech_started':
      return { kind: 'speechStarted' }
    case 'input_audio_buffer.speech_stopped':
      return { kind: 'speechStopped' }
    case 'input_audio_buffer.committed':
      return { kind: 'committed' }
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
