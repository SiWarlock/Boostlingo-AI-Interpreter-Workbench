import type { RealtimeTokenRequest, RealtimeTokenResponse } from '../types/domain'
import { request } from './http'

// POST /api/realtime/client-secret (ARCH-009 §6) — mints the short-lived ephemeral Realtime credential
// (`ek_…`) from OUR backend (which holds the standard key, invariant #1). JSON over the shared `http`
// boundary, so a failure surfaces as ApiError(UiError) with no raw-body leak (web lesson §3). The returned
// `ek_` is handed to the WebRTC client (realtimeWebRtcClient) and used transiently to authorize the SDP
// exchange; it is NEVER persisted to the store (invariant #2) — this client only fetches it.
export const realtimeApi = {
  mintClientSecret(req: RealtimeTokenRequest): Promise<RealtimeTokenResponse> {
    // Build the body conditionally so an undefined model is OMITTED (the backend resolves the configured
    // default), not sent as `model: undefined` — ARCH-009 §6 optional model.
    const body: Record<string, unknown> = {
      sessionId: req.sessionId,
      direction: req.direction,
    }
    if (req.model !== undefined) {
      body.model = req.model
    }

    return request<RealtimeTokenResponse>('/api/realtime/client-secret', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
  },
}
