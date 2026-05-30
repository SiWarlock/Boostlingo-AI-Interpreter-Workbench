// Frontend TS projection of the backend domain + API contracts (ARCH-005 / ARCH-007 / ARCH-009,
// Appendix A). The backend serializes camelCase + enum-as-camelCase-string + explicit-null
// (server lesson §2 `JsonDefaults`), so these are camelCase mirrors. The architecture doc is the
// canonical contract; these types are the executable enforcement. When a slice changes a field,
// flag it at Step 9 so the orchestrator pairs the ARCHITECTURE.md edit (cross-doc invariant).

// --- Enums (camelCase string unions mirroring ARCH-005) ---

export type InterpretationMode = 'realtime' | 'cascade'
export type LanguageCode = 'en' | 'es'
export type SessionStatus =
  | 'idle'
  | 'configured'
  | 'starting'
  | 'active'
  | 'readyForTurn'
  | 'ending'
  | 'ended'
export type TurnStatus =
  | 'ready'
  | 'recording'
  | 'captured'
  | 'processing'
  | 'playing'
  | 'completed'
  | 'failed'
export type LatencyStage =
  | 'capture'
  | 'realtime'
  | 'stt'
  | 'translation'
  | 'tts'
  | 'playback'
  | 'persistence'
  | 'evaluation'
  | 'overall'
export type ClockSource = 'server' | 'browser'

export type RealtimeModel = 'gpt-realtime' | 'gpt-realtime-mini'
export type TranslationModel = 'gpt-5.4-nano' | 'gpt-5.4-mini'

export type LanguageDirection = { source: LanguageCode; target: LanguageCode }

// --- View models (ARCH-007 §4 — UI renders only from these; verbatim shapes) ---

// Frontend projection of ProviderError AFTER the backend sanitizer — never carries raw provider
// text/stacks (ARCH-007 / ARCH-018 / ARCH-019).
export type UiError = {
  code: string
  safeMessage: string
  stage?: string
  retryable: boolean
  turnId?: string
}

export type TurnViewModel = {
  turnId: string
  mode: InterpretationMode
  direction: LanguageDirection
  status: TurnStatus
  startedAt: string
  completedAt?: string
  audioDurationMs?: number
  sourceTranscript: { text: string; isFinal: boolean }[]
  targetTranscript: { text: string; isFinal: boolean }[]
  latency: {
    speechEndToFirstAudioMs?: number
    speechEndToPlaybackMs?: number
    totalTurnMs?: number
    stages?: Record<string, number>
  }
  // Raw latency timeline (with absolute timestamps), retained so deriveTurnMetrics can compute the
  // top-level client-timing deltas via absolute-timestamp Between (the `stages` map above keeps only
  // relativeMs, which must never be used for cross-event math — lesson §7 / D.6). (D.6)
  latencyEvents?: LatencyEvent[]
  estimatedCostUsd?: number
  estimatedCostPerMinuteUsd?: number
  translationModelUsed?: string
  // Full cost estimate retained for the CostPanel (model + assumptions tooltip). (D.6)
  cost?: CostEstimate
  werWer?: number
  errors: UiError[]
}

export type UiSessionState = {
  sessionId: string | null
  label?: string
  mode: InterpretationMode
  direction: LanguageDirection
  realtimeModel: RealtimeModel
  translationModel: TranslationModel
  // canonical names mirror ARCH-005 SessionStatus/TurnStatus:
  sessionStatus: SessionStatus
  turnStatus: TurnStatus
  providerHealth?: ConfigResponse // from GET /api/config
  turns: TurnViewModel[]
  currentTurn?: TurnViewModel
  summary?: SessionSummary
  errors: UiError[]
}

// --- Deferred wire mirrors (pragmatic-accrete) ---
// Opaque until the slice that first RENDERS them tightens the shape against Appendix A. The frontend
// builds TurnViewModel from the cascade WS, not the raw wire turn, so this may stay opaque.
export type InterpretationTurn = Record<string, unknown>

// --- Session summary mirrors (GET /api/sessions/{id}/summary — ARCH-009 / Appendix A) ---
// camelCase mirrors of the backend SessionSummary/ModeSummary/WerSummary (Sessions/SessionModels.cs).
// Nullable backend doubles serialize as explicit null (JsonDefaults) → `?: number | null`. Tightened
// from D.1's opaque Record at D.6 (MetricsPanel session averages); F.3 (ComparisonSummary) reuses.
export type ModeSummary = {
  turnCount: number
  avgSpeechEndToFirstAudioMs?: number | null
  avgSpeechEndToPlaybackMs?: number | null
  estimatedCostPerMinuteUsd?: number | null
  errorCount: number
  avgSttFinalMs?: number | null
  avgTranslationFinalMs?: number | null
  avgTtsFirstAudioMs?: number | null
}

export type WerSummary = { sampleCount: number; avgWer: number }

export type SessionSummary = {
  turnCount: number
  realtime?: ModeSummary | null
  cascade?: ModeSummary | null
  wer?: WerSummary | null
  computedAt: string
  pricingConfigVersion: string
}

// --- Wire DTOs the D.1 clients parse (ARCH-009 / Appendix A) ---

export type RealtimeCapability = { configured: boolean; models: string[] }
export type SttCapability = { configured: boolean; provider: string; model: string }
export type TranslationCapability = { configured: boolean; provider: string; models: string[] }
export type TtsCapability = { configured: boolean; provider: string; model: string }
export type CascadeCapability = {
  stt: SttCapability
  translation: TranslationCapability
  tts: TtsCapability
}

// GET /api/config — capability flags from provider-key PRESENCE only (never values, invariant #1).
export type ConfigResponse = {
  realtime: RealtimeCapability
  cascade: CascadeCapability
  languages: string[]
  pricingConfigVersion: string
}

// POST /api/sessions request. The server assembles the full ProviderProfile from these + Options;
// the client supplies only the selectable models.
export type CreateSessionRequest = {
  label?: string
  mode: InterpretationMode
  direction: LanguageDirection
  realtimeModel: string
  translationModel: string
}

// Server-assembled provider profile (full flat mirror — small + stable; D.2/D.6 read more of it).
export type ProviderProfile = {
  realtimeProvider: string
  realtimeModel: string
  sttProvider: string
  sttModel: string
  sttLanguage: string
  translationProvider: string
  translationModel: string
  ttsProvider: string
  ttsModel: string
  ttsVoice: string
}

export type SessionConfig = {
  currentMode: InterpretationMode
  direction: LanguageDirection
  providerProfile: ProviderProfile
}

// POST /api/sessions + GET /api/sessions/{id} response. Top-level id is `sessionId` (NOT `id`);
// mode/direction/models nest under config.*. turns/modeTransitions/summary deferred (opaque).
export type InterpretationSession = {
  sessionId: string
  label?: string
  startedAt: string
  endedAt?: string
  config: SessionConfig
  turns: InterpretationTurn[]
  modeTransitions: unknown[]
  summary?: SessionSummary
  pricingConfigVersion: string
}

// POST /api/sessions/{id}/turns response.
export type CreateTurnResponse = { turnId: string }

// POST /api/sessions/{id}/end response (Flow F). Exactly one of path/warning is non-null; 200 never 500.
export type EndSessionResponse = {
  session: InterpretationSession
  persistedPath?: string
  persistenceWarning?: UiError
}

// POST /api/cascade/turn — the params the cascade client packs into the multipart form. `source` +
// `target` are SEPARATE fields (CascadeTurnForm), not a nested direction; always sent (C.5 mitigation).
export type CascadeTurnParams = {
  sessionId: string
  turnId?: string
  source: LanguageCode
  target: LanguageCode
  translationModel: string
  ttsVoice: string
}

// POST /api/cascade/turn response. Audio is in-body base64 (RESPONSE-only — never persisted, invariant #3).
export type CascadeTurnResponse = {
  turn: InterpretationTurn
  audioBase64?: string
  audioContentType?: string
  persistenceWarning?: UiError
}

// --- Streaming wire payloads (cascade WS messages + turn-record fields, ARCH-005 mirrors) ---
// These were inside the opaque InterpretationTurn in D.1; D.4a tightens them as the cascade WS client
// + streaming store actions consume them. camelCase mirrors of the backend records.

export type TranscriptSegment = {
  segmentId: string
  role: 'source' | 'target'
  text: string
  isFinal: boolean
  provider: string
  timestamp: string
  clockSource: ClockSource
}

export type LatencyEvent = {
  name: string
  stage: LatencyStage
  timestamp: string
  relativeMs: number
  clockSource: ClockSource
  metadata: Record<string, string>
}

export type CostEstimate = {
  provider: string
  model: string
  pricingBasis: string
  estimatedUsd: number
  estimatedUsdPerMinute: number | null
  units: Record<string, number>
  pricingConfigVersion: string
  assumptions: string[]
}

// The normalized, UI-safe provider error carried by the cascade `error` WS frame (already sanitized
// backend-side). Projected to a lean UiError before it reaches the store (drops provider/httpStatusCode).
export type ProviderError = {
  provider: string
  stage: string
  code: string
  safeMessage: string
  retryable: boolean
  httpStatusCode?: number
}
