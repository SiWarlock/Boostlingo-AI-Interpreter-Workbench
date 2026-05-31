import type {
  CreateSessionRequest,
  CreateTurnResponse,
  EndSessionResponse,
  InterpretationMode,
  InterpretationSession,
  InterpretationTurn,
  LatencyEvent,
  SessionSummary,
} from '../types/domain'
import { request } from './http'

// Session + turn lifecycle (ARCH-009). The D.1-relevant set only: create/get/createTurn/end. The
// realtime finalize paths (/complete, /events) are Phase E. Backend owns turnId for all modes.
export const sessionsApi = {
  createSession(body: CreateSessionRequest): Promise<InterpretationSession> {
    return request<InterpretationSession>('/api/sessions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
  },

  getSession(sessionId: string): Promise<InterpretationSession> {
    return request<InterpretationSession>(`/api/sessions/${encodeURIComponent(sessionId)}`, {
      method: 'GET',
    })
  },

  createTurn(sessionId: string): Promise<CreateTurnResponse> {
    return request<CreateTurnResponse>(`/api/sessions/${encodeURIComponent(sessionId)}/turns`, {
      method: 'POST',
    })
  },

  endSession(sessionId: string): Promise<EndSessionResponse> {
    return request<EndSessionResponse>(`/api/sessions/${encodeURIComponent(sessionId)}/end`, {
      method: 'POST',
    })
  },

  // POST /api/sessions/{id}/mode (Finding 2c — ARCH-009 / ARCH-017 Flow G). Propagates a mid-session mode
  // switch to the backend so a turn created AFTER the switch is stamped with the new mode (else the by-mode
  // comparison is invalid); the backend records a ModeTransitionEvent + returns the updated session (body
  // is the SetModeRequest mirror { mode }). The frontend resyncs config.currentMode from the response.
  setMode(sessionId: string, mode: InterpretationMode): Promise<InterpretationSession> {
    return request<InterpretationSession>(`/api/sessions/${encodeURIComponent(sessionId)}/mode`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mode }),
    })
  },

  // Backend-canonical session aggregation (ARCH-009) — the MetricsPanel's "session averages by mode"
  // reads ModeSummary from here (per-stage avgs are present for cascade; AvgSpeechEnd* is n/a for
  // cascade — the backend has no client-timing for it). Fetched on turn-completion + manual refresh.
  getSummary(sessionId: string): Promise<SessionSummary> {
    return request<SessionSummary>(`/api/sessions/${encodeURIComponent(sessionId)}/summary`, {
      method: 'GET',
    })
  },

  // POST /api/sessions/{id}/turns/{turnId}/events (ARCH-009 §6; B.9c-ii) — reports a turn's client-stamped
  // latency events to the backend, which persists them (with clockSource) + aggregates the canonical
  // realtime metrics (the §13 realtime half: realtime CAN report client timing, unlike cascade). Body
  // { events } matches the backend AppendEventsRequest; the endpoint returns the updated InterpretationTurn
  // (Ok(turn)). The realtime E.4 reporting path; cascade is priced/timed over its WS instead.
  appendTurnEvents(
    sessionId: string,
    turnId: string,
    events: LatencyEvent[],
  ): Promise<InterpretationTurn> {
    return request<InterpretationTurn>(
      `/api/sessions/${encodeURIComponent(sessionId)}/turns/${encodeURIComponent(turnId)}/events`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ events }),
      },
    )
  },
}
