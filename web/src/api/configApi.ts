import type { ConfigResponse } from '../types/domain'
import { request } from './http'

// GET /api/config (ARCH-009). Capability flags from provider-key presence only — consumed by the
// App on-mount bootstrap (Flow A) + D.2 config-gating.
export const configApi = {
  getConfig(): Promise<ConfigResponse> {
    return request<ConfigResponse>('/api/config', { method: 'GET' })
  },
}
