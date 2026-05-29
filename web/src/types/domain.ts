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
  estimatedCostUsd?: number
  estimatedCostPerMinuteUsd?: number
  translationModelUsed?: string
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
// Opaque until the slice that first RENDERS them tightens the shape against Appendix A. D.1 only
// passes these through the clients / carries them on state; it never reads their fields.
export type SessionSummary = Record<string, unknown> // F.3 ComparisonSummary tightens
export type InterpretationTurn = Record<string, unknown> // D.4/D.6 render → tighten then

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
