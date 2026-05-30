import type {
  CreateSessionRequest,
  CreateTurnResponse,
  EndSessionResponse,
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
