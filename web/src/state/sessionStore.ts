import { useSyncExternalStore } from 'react'
import type {
  ConfigResponse,
  CostEstimate,
  InterpretationMode,
  InterpretationSession,
  LanguageDirection,
  LatencyEvent,
  RealtimeModel,
  SessionStatus,
  SessionSummary,
  TranscriptSegment,
  TranslationModel,
  TurnStatus,
  TurnViewModel,
  UiError,
  UiSessionState,
} from '../types/domain'

// The mode-agnostic UI state store (ARCH-007). UI components render ONLY from this state; the
// transports (cascade WS / realtime WebRTC clients) own all wire detail. The store OWNS the
// sessionStatus/turnStatus state machine (those are NOT on the wire) + is the single error sink.
//
// State is IMMUTABLE: every action produces a NEW state object (reference change) so
// useSyncExternalStore re-renders correctly; getState() returns a stable reference between actions
// so getSnapshot doesn't loop.

// A partial patch of the user-selectable session config (D.2). The setup form writes each edit into
// the store via updateSessionConfig; Start reads the merged result. Replaces the D.1 configureSession
// (which had no live caller — D.2 chose the merging action).
export type SessionConfigPatch = Partial<
  Pick<UiSessionState, 'label' | 'mode' | 'direction' | 'realtimeModel' | 'translationModel'>
>

export type SessionStore = {
  getState(): UiSessionState
  subscribe(listener: () => void): () => void
  loadConfig(config: ConfigResponse): void
  updateSessionConfig(patch: SessionConfigPatch): void
  sessionStarted(session: InterpretationSession): void
  setSessionStatus(status: SessionStatus): void
  setTurnStatus(status: TurnStatus): void
  addError(error: UiError): void
  clearErrors(): void
  setSummary(summary: SessionSummary): void
  sessionEnded(): void
  reset(): void
  // --- Streaming turn actions (D.4a) — driven by the cascade WS client's dispatch ---
  beginTurn(input: { turnId: string; mode: InterpretationMode; direction: LanguageDirection }): void
  appendTranscriptSegment(segment: TranscriptSegment): void
  appendLatencyEvent(event: LatencyEvent): void
  setTurnCost(estimate: CostEstimate): void
  failTurn(error: UiError): void
  completeTurn(turnId: string, status: TurnStatus): void
}

// Append a transcript segment to a role's running list: replace the trailing non-final partial (a
// later partial supersedes it; a final supersedes + finalizes it), but start a NEW running entry once
// the trailing entry is final — so the panel renders "running partial -> finalized history" (ARCH-011).
function appendSegment(
  list: { text: string; isFinal: boolean }[],
  segment: TranscriptSegment,
): { text: string; isFinal: boolean }[] {
  const next = [...list]
  const entry = { text: segment.text, isFinal: segment.isFinal }
  const last = next[next.length - 1]
  if (last && !last.isFinal) {
    next[next.length - 1] = entry
  } else {
    next.push(entry)
  }
  return next
}

function createInitialState(): UiSessionState {
  return {
    sessionId: null,
    mode: 'cascade', // Phase D builds the cascade UI first; config-gating (D.2) disables an unconfigured mode
    direction: { source: 'en', target: 'es' },
    realtimeModel: 'gpt-realtime',
    translationModel: 'gpt-5.4-nano',
    sessionStatus: 'idle',
    turnStatus: 'ready',
    turns: [],
    errors: [],
  }
}

export function createSessionStore(): SessionStore {
  let state: UiSessionState = createInitialState()
  const listeners = new Set<() => void>()

  const set = (next: UiSessionState): void => {
    state = next
    for (const listener of listeners) {
      listener()
    }
  }

  return {
    getState: () => state,
    subscribe: (listener) => {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    loadConfig: (config) => set({ ...state, providerHealth: config }),
    // Merge a partial config patch. Transition idle -> configured on first config, but NEVER drag an
    // already-started session backwards: a between-turns mode switch (Flow G, ARCH-007 inv. 9) writes
    // the new mode while the session stays 'active'. One merging action (store = live config
    // source-of-truth, ARCH-007) beats granular per-field setters.
    updateSessionConfig: (patch) =>
      set({
        ...state,
        ...patch,
        sessionStatus: state.sessionStatus === 'idle' ? 'configured' : state.sessionStatus,
      }),
    // Maps the wire InterpretationSession DTO -> view state. The model strings come back from the
    // catalog-constrained create request, so the narrowing cast at this wire->view boundary is sound.
    sessionStarted: (session) =>
      set({
        ...state,
        sessionId: session.sessionId,
        label: session.label,
        mode: session.config.currentMode,
        direction: session.config.direction,
        realtimeModel: session.config.providerProfile.realtimeModel as RealtimeModel,
        translationModel: session.config.providerProfile.translationModel as TranslationModel,
        sessionStatus: 'active',
      }),
    setSessionStatus: (status) => set({ ...state, sessionStatus: status }),
    setTurnStatus: (status) => set({ ...state, turnStatus: status }),
    addError: (error) => set({ ...state, errors: [...state.errors, error] }),
    clearErrors: () => set({ ...state, errors: [] }),
    // The backend-canonical session aggregate (GET /summary) — the MetricsPanel session-averages
    // source. Held on state so the panel renders only from the store (ARCH-007), D.6.
    setSummary: (summary) => set({ ...state, summary }),
    sessionEnded: () => set({ ...state, sessionStatus: 'ended' }),
    reset: () => set(createInitialState()),

    beginTurn: ({ turnId, mode, direction }) =>
      set({
        ...state,
        currentTurn: {
          turnId,
          mode,
          direction,
          status: 'recording',
          startedAt: new Date().toISOString(),
          sourceTranscript: [],
          targetTranscript: [],
          latency: {},
          latencyEvents: [],
          errors: [],
        },
        turnStatus: 'recording',
      }),

    appendTranscriptSegment: (segment) => {
      const turn = state.currentTurn
      if (!turn) {
        return // no active turn — ignore (defensive; transcripts arrive only between begin and complete)
      }
      const updated =
        segment.role === 'target'
          ? { ...turn, targetTranscript: appendSegment(turn.targetTranscript, segment) }
          : { ...turn, sourceTranscript: appendSegment(turn.sourceTranscript, segment) }
      set({ ...state, currentTurn: updated })
    },

    // Retain BOTH projections of each latency event: `stages` (per-event relativeMs, for the per-stage
    // display passthrough) AND `latencyEvents` (the raw timeline with absolute timestamps). The
    // top-level speech-end deltas are computed by deriveTurnMetrics from the raw timestamps — NEVER
    // from relativeMs (lesson §7: relativeMs is per-event display, not a cross-event math input). For
    // cascade the backend can't compute these (no client->server latency channel), so the frontend's
    // browser-clock markers (recording.started/stopped, playback.started, turn.completed) are the only
    // source; the backend GET /summary still owns the session averages.
    appendLatencyEvent: (event) => {
      const turn = state.currentTurn
      if (!turn) {
        return
      }
      set({
        ...state,
        currentTurn: {
          ...turn,
          latency: {
            ...turn.latency,
            stages: { ...turn.latency.stages, [event.name]: event.relativeMs },
          },
          latencyEvents: [...(turn.latencyEvents ?? []), event],
        },
      })
    },

    setTurnCost: (estimate) => {
      const turn = state.currentTurn
      if (!turn) {
        return
      }
      set({
        ...state,
        currentTurn: {
          ...turn,
          estimatedCostUsd: estimate.estimatedUsd,
          estimatedCostPerMinuteUsd: estimate.estimatedUsdPerMinute ?? undefined,
          translationModelUsed: estimate.model,
          cost: estimate, // full estimate for the CostPanel (model + the assumptions tooltip), D.6
        },
      })
    },

    failTurn: (error) =>
      set({
        ...state,
        errors: [...state.errors, error],
        currentTurn: state.currentTurn
          ? { ...state.currentTurn, status: 'failed', errors: [...state.currentTurn.errors, error] }
          : state.currentTurn,
        turnStatus: 'failed',
      }),

    completeTurn: (turnId, status) => {
      const nextTurnStatus: TurnStatus = status === 'failed' ? 'failed' : 'completed'
      const turn = state.currentTurn
      if (turn) {
        // A done for a DIFFERENT turn than the active one is stale — ignore it entirely (don't strand
        // the active turn or flip turnStatus out from under it).
        if (turn.turnId !== turnId) {
          return
        }
        // The backend stamps turn.completed on finalize but does NOT stream it over the cascade WS —
        // the frontend's turn-complete signal is the WS `done` -> completeTurn. Stamp a browser-clock
        // turn.completed onto the timeline here (the canonical totalTurn endpoint; D.6) BEFORE the turn
        // moves into turns[]. deriveTurnMetrics prefers it; tts.complete (server) is the fallback.
        const now = new Date().toISOString()
        const completedEvent: LatencyEvent = {
          name: 'turn.completed',
          stage: 'overall',
          timestamp: now,
          relativeMs: 0,
          clockSource: 'browser',
          metadata: {},
        }
        const finalized: TurnViewModel = {
          ...turn,
          status,
          completedAt: now,
          latencyEvents: [...(turn.latencyEvents ?? []), completedEvent],
        }
        set({
          ...state,
          turns: [...state.turns, finalized],
          currentTurn: undefined,
          turnStatus: nextTurnStatus,
        })
      } else {
        // No active turn — just reflect completion at the session turn-status level.
        set({ ...state, turnStatus: nextTurnStatus })
      }
    },
  }
}

// Module singleton — the app's one store instance.
export const sessionStore: SessionStore = createSessionStore()

// React glue (manual-smoke-exempt). Returns the whole state; selector-based subscriptions are
// deferred to D.6 (panels) to avoid the useSyncExternalStore getSnapshot-stability footgun. getState
// is stable between actions, so this never loops.
export function useSessionState(): UiSessionState {
  return useSyncExternalStore(sessionStore.subscribe, sessionStore.getState, sessionStore.getState)
}
