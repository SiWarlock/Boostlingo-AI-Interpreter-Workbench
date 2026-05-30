import type {
  CreateSessionRequest,
  CreateTurnResponse,
  EndSessionResponse,
  InterpretationSession,
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
}
