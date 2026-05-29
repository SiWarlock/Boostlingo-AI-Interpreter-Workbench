import type { CascadeTurnParams, CascadeTurnResponse } from '../types/domain'
import { request } from './http'

// POST /api/cascade/turn — the non-streaming blob fallback (ARCH-009 / C.5). Packs the turn params
// + audio into multipart form-data. `source` + `target` are ALWAYS sent (the backend CascadeTurnForm
// lacks [Required] on them, so a missing direction silently binds En->En — mitigate client-side).
// We pass FormData as the body and set NO content-type header, so fetch sets the multipart boundary.
export const cascadeApi = {
  postCascadeTurn(params: CascadeTurnParams, audio: Blob): Promise<CascadeTurnResponse> {
    const form = new FormData()
    form.set('sessionId', params.sessionId)
    if (params.turnId !== undefined) {
      form.set('turnId', params.turnId)
    }
    form.set('source', params.source)
    form.set('target', params.target)
    form.set('translationModel', params.translationModel)
    form.set('ttsVoice', params.ttsVoice)
    form.set('audio', audio, 'audio')

    return request<CascadeTurnResponse>('/api/cascade/turn', {
      method: 'POST',
      body: form,
    })
  },
}
