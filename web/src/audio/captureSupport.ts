import type { UiError } from '../types/domain'

// Blob-fallback recording duration bounds. The cap keeps a single record from holding the mic open +
// accumulating chunks without limit (resource-exhaustion guard at the public boundary).
export const DEFAULT_BLOB_DURATION_MS = 4000
export const MAX_BLOB_DURATION_MS = 60_000

export function clampBlobDurationMs(ms: number): number {
  if (!Number.isFinite(ms) || ms <= 0) {
    return DEFAULT_BLOB_DURATION_MS
  }
  return Math.min(ms, MAX_BLOB_DURATION_MS)
}

// MediaRecorder mimeType probe for the blob fallback (ARCH-030). Probe in priority order and send the
// ACTUAL supported type — never hardcode the container (Safari < 18.4 supports mp4/AAC, not webm).
const MIME_PROBE_ORDER = [
  'audio/webm;codecs=opus',
  'audio/webm',
  'audio/mp4',
  'audio/ogg;codecs=opus',
] as const

export function probeRecorderMimeType(isTypeSupported: (type: string) => boolean): string | null {
  for (const type of MIME_PROBE_ORDER) {
    if (isTypeSupported(type)) {
      return type
    }
  }
  return null
}

// getUserMedia rejection -> an actionable, sanitized UiError (ARCH-018 frontend error philosophy).
// Frontend-coined codes (getUserMedia never reaches the backend); the raw DOMException message is
// never echoed. All three are retryable — a retry can succeed after the user grants/connects.
export function micErrorToUiError(error: unknown): UiError {
  const name =
    typeof error === 'object' && error !== null && 'name' in error
      ? String((error as { name: unknown }).name)
      : ''

  if (name === 'NotAllowedError' || name === 'SecurityError') {
    return {
      code: 'mic.permission_denied',
      safeMessage: 'Microphone permission denied. Enable mic access and retry.',
      retryable: true,
    }
  }
  if (name === 'NotFoundError') {
    return {
      code: 'mic.not_found',
      safeMessage: 'No microphone was found. Connect one and retry.',
      retryable: true,
    }
  }
  return {
    code: 'mic.unavailable',
    safeMessage: 'The microphone is unavailable. Check your device and retry.',
    retryable: true,
  }
}
