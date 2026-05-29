import { useSyncExternalStore } from 'react'
import type {
  ConfigResponse,
  InterpretationSession,
  RealtimeModel,
  SessionStatus,
  TranslationModel,
  TurnStatus,
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
  sessionEnded(): void
  reset(): void
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
    sessionEnded: () => set({ ...state, sessionStatus: 'ended' }),
    reset: () => set(createInitialState()),
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
