import type {
  EvaluationPhrase,
  TranscribeParams,
  TranscribeResponse,
  WerRequest,
  WerResponse,
} from '../types/domain'
import { request } from './http'

// The evaluation HTTP client (ARCH-009 / ARCH-015) — three endpoints over the shared http boundary:
// GET /phrases (the scripted phrases), POST /transcribe (STT-only hypothesis, multipart), POST /wer
// (WER score + optional turn-attach persist). WER is STT-only (Flow D). All wire detail lives here; the
// panel renders from local state + the DI'd evaluationActions flow (clean separation, ARCH-007).
export const evaluationApi = {
  getPhrases(): Promise<EvaluationPhrase[]> {
    return request<EvaluationPhrase[]>('/api/evaluation/phrases', { method: 'GET' })
  },

  // Multipart like cascadeApi: pass FormData as the body and set NO content-type header so fetch sets
  // the multipart boundary itself. The audio Blob's container drives the backend's STT encoding routing.
  transcribe(params: TranscribeParams, audio: Blob): Promise<TranscribeResponse> {
    const form = new FormData()
    form.set('sessionId', params.sessionId)
    form.set('phraseId', params.phraseId)
    form.set('language', params.language)
    form.set('audio', audio, 'audio')

    return request<TranscribeResponse>('/api/evaluation/transcribe', {
      method: 'POST',
      body: form,
    })
  },

  computeWer(req: WerRequest): Promise<WerResponse> {
    return request<WerResponse>('/api/evaluation/wer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    })
  },
}
