import type { LanguageCode } from '../types/domain'
import { ApiError } from '../api/http'

// The cached-audio loader for the G.4 soak harness (decision 4 + Q3). Synthesizes each scripted utterance
// ONCE via the dev TTS endpoint (086), decodes it, and caches by (text, language) so the IDENTICAL audio is
// reused across both modes (fair + repeatable). In-memory per run (Q3). The real fetch/decode is
// manual-smoke; the cache-key / fetch-once dedup / evict-on-failure orchestration is unit-tested.

export type SoakAudioCacheDeps = {
  // Browser `AudioContext.decodeAudioData` bound to the harness-owned context (089 supplies it; Q4).
  decodeAudio: (bytes: ArrayBuffer) => Promise<AudioBuffer>
  // Defaults to the real dev-TTS loader; injected as a fake in tests.
  fetchAudioBytes?: (text: string, language: LanguageCode) => Promise<ArrayBuffer>
}

export type SoakAudioCache = {
  load: (text: string, language: LanguageCode) => Promise<AudioBuffer>
}

// Read per-call (web §3) — empty base ⇒ relative path, served by the Vite dev proxy (ARCH-029).
function baseUrl(): string {
  return import.meta.env.VITE_API_BASE_URL ?? ''
}

// The default real loader: POST the dev TTS endpoint and read the raw WAV BYTES. dev/tts returns
// `Results.File` bytes (NOT JSON), so this does NOT go through the `http` helper (which reads
// `response.json()`); it keeps the SAME no-leak failure boundary (web §3) — a non-OK status or a fetch
// rejection surfaces as a sanitized ApiError, never a raw body / TypeError.
export async function fetchDevTtsAudio(text: string, language: LanguageCode): Promise<ArrayBuffer> {
  let response: Response
  try {
    response = await fetch(`${baseUrl()}/api/dev/tts`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ text, language }),
    })
  } catch {
    throw new ApiError({
      code: 'network.error',
      safeMessage: 'Could not reach the server. Check that the backend is running and retry.',
      retryable: true,
    })
  }
  if (!response.ok) {
    throw new ApiError({
      code: 'dev_tts.failed',
      safeMessage: 'Dev TTS synthesis failed.',
      retryable: response.status === 429 || response.status >= 500,
    })
  }
  return response.arrayBuffer()
}

export function createSoakAudioCache(deps: SoakAudioCacheDeps): SoakAudioCache {
  const fetchAudioBytes = deps.fetchAudioBytes ?? fetchDevTtsAudio
  const cache = new Map<string, Promise<AudioBuffer>>()

  function load(text: string, language: LanguageCode): Promise<AudioBuffer> {
    const key = `${language}::${text}`
    const cached = cache.get(key)
    if (cached) {
      return cached
    }
    // Cache the PROMISE (not the resolved value) so concurrent loads of the same key share ONE fetch.
    const pending = (async () => {
      const bytes = await fetchAudioBytes(text, language)
      return deps.decodeAudio(bytes)
    })()
    cache.set(key, pending)
    // On failure, EVICT so a later retry re-fetches — never serve a poisoned half-entry. The rejection
    // still propagates to the caller via the returned `pending`. The identity guard avoids evicting a
    // newer entry if one raced in for the same key.
    pending.catch(() => {
      if (cache.get(key) === pending) {
        cache.delete(key)
      }
    })
    return pending
  }

  return { load }
}
