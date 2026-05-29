import type { CascadeAudioChunk } from '../cascade/cascadeStreamClient'
import { sessionStore } from '../state/sessionStore'
import type { LatencyEvent } from '../types/domain'

// Cascade TTS playback (ARCH-030): progressive MediaSource (mp3) with a blob-HTMLAudioElement
// fallback; stamps `playback.started` on the `playing` event (once/turn, ARCH-013); no overlapping
// playback. The MSE/SourceBuffer/HTMLAudioElement wiring is a manual-smoke browser shell over the
// pure helpers below (the D.3 pattern). Raw audio is held transiently for playback and NEVER enters
// the store — only the `playback.started` latency marker does (invariant-#3 discipline on the client).

// --- Pure, test-first helpers ---

// Decode a base64 `audio` chunk to bytes (for MSE append / blob assembly).
export function decodeBase64Audio(base64: string): Uint8Array<ArrayBuffer> {
  const binary = atob(base64)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }
  return bytes
}

// Clamp a server-supplied audio content-type to a known allowlist before it reaches MediaSource /
// Blob (defense-in-depth at the data boundary, ARCH-019; the backend already clamps in C.4b). Params
// are stripped (the cascade sends bare `audio/mpeg`); anything off-list falls back to the default.
const DEFAULT_AUDIO_CONTENT_TYPE = 'audio/mpeg'
const ALLOWED_AUDIO_CONTENT_TYPES = new Set([
  'audio/mpeg',
  'audio/mp4',
  'audio/ogg',
  'audio/webm',
  'audio/wav',
  'audio/aac',
  'audio/flac',
])

export function clampAudioContentType(contentType: string): string {
  const normalized = contentType.split(';')[0].trim().toLowerCase()
  return ALLOWED_AUDIO_CONTENT_TYPES.has(normalized) ? normalized : DEFAULT_AUDIO_CONTENT_TYPE
}

// No-overlap guard: at most one active playback. Beginning a new one stops the prior (ARCH-030).
export type PlaybackGuard = {
  begin(stopPrior: () => void): void
  clear(): void
  isActive(): boolean
}

export function createPlaybackGuard(): PlaybackGuard {
  let active = false
  let stopCurrent: (() => void) | null = null
  return {
    begin(stopPrior) {
      if (active && stopCurrent) {
        stopCurrent()
      }
      active = true
      stopCurrent = stopPrior
    },
    clear() {
      active = false
      stopCurrent = null
    },
    isActive() {
      return active
    },
  }
}

// The `playback.started` latency marker (ARCH-013) — browser-clock; `relativeMs` is a placeholder (the
// client has no backend turn-origin; the aggregator uses the absolute `timestamp`, lesson §7).
export function buildPlaybackStartedEvent(timestamp: string): LatencyEvent {
  return {
    name: 'playback.started',
    stage: 'playback',
    timestamp,
    relativeMs: 0,
    clockSource: 'browser',
    metadata: {},
  }
}

// Stamps `playback.started` once per turn (the `playing` event can fire more than once); re-armed by reset.
export type PlaybackStartedStamper = { stamp(timestamp: string): void; reset(): void }

export function createPlaybackStartedStamper(
  append: (event: LatencyEvent) => void,
): PlaybackStartedStamper {
  let stamped = false
  return {
    stamp(timestamp) {
      if (stamped) {
        return
      }
      stamped = true
      append(buildPlaybackStartedEvent(timestamp))
    },
    reset() {
      stamped = false
    },
  }
}

// --- The controller (manual-smoke browser shell) ---

export type PlaybackController = {
  enqueue(chunk: CascadeAudioChunk): void
  reset(): void
}

type PlaybackStore = { appendLatencyEvent: (event: LatencyEvent) => void }

export function createPlaybackController(deps: { store: PlaybackStore }): PlaybackController {
  const guard = createPlaybackGuard()
  const stamper = createPlaybackStartedStamper(deps.store.appendLatencyEvent)
  let audioEl: HTMLAudioElement | null = null
  let mediaSource: MediaSource | null = null
  let sourceBuffer: SourceBuffer | null = null
  let objectUrl: string | null = null
  let useBlobFallback = false
  let contentType = DEFAULT_AUDIO_CONTENT_TYPE
  const allChunks: Uint8Array<ArrayBuffer>[] = [] // retained for the blob fallback (transient, never persisted)
  const appendQueue: Uint8Array<ArrayBuffer>[] = []

  function onPlaying(): void {
    stamper.stamp(new Date().toISOString())
  }

  function reset(): void {
    if (audioEl) {
      audioEl.pause()
      audioEl.removeEventListener('playing', onPlaying)
      audioEl.removeAttribute('src')
      audioEl.load()
      audioEl = null
    }
    if (objectUrl) {
      URL.revokeObjectURL(objectUrl)
      objectUrl = null
    }
    mediaSource = null
    sourceBuffer = null
    useBlobFallback = false
    contentType = DEFAULT_AUDIO_CONTENT_TYPE
    allChunks.length = 0
    appendQueue.length = 0
    stamper.reset()
    guard.clear()
  }

  function switchToBlob(): void {
    useBlobFallback = true
    if (!audioEl) {
      return
    }
    if (objectUrl) {
      URL.revokeObjectURL(objectUrl)
    }
    // Uint8Array is a valid BlobPart; the Blob copies the bytes (no need to detach the views).
    const blob = new Blob(allChunks, { type: contentType })
    objectUrl = URL.createObjectURL(blob)
    audioEl.src = objectUrl
    void audioEl.play()
  }

  function pumpAppend(): void {
    if (!sourceBuffer || sourceBuffer.updating || appendQueue.length === 0) {
      return
    }
    const next = appendQueue.shift()
    if (!next) {
      return
    }
    try {
      sourceBuffer.appendBuffer(next)
    } catch {
      switchToBlob() // MSE append failed -> blob fallback (ARCH-030)
    }
  }

  function ensurePlayback(): void {
    if (audioEl) {
      return
    }
    guard.begin(reset) // single active playback — stops any prior
    audioEl = new Audio()
    audioEl.addEventListener('playing', onPlaying)
    try {
      if (typeof MediaSource === 'undefined' || !MediaSource.isTypeSupported(contentType)) {
        throw new Error('MediaSource unsupported')
      }
      mediaSource = new MediaSource()
      objectUrl = URL.createObjectURL(mediaSource)
      audioEl.src = objectUrl
      mediaSource.addEventListener('sourceopen', () => {
        if (!mediaSource) {
          return
        }
        sourceBuffer = mediaSource.addSourceBuffer(contentType)
        sourceBuffer.addEventListener('updateend', pumpAppend)
        pumpAppend()
      })
    } catch {
      useBlobFallback = true
    }
    if (!useBlobFallback) {
      void audioEl.play() // MSE path: data arrives via appendBuffer (blob path plays via switchToBlob)
    }
  }

  function enqueue(chunk: CascadeAudioChunk): void {
    contentType = clampAudioContentType(chunk.contentType)
    const bytes = decodeBase64Audio(chunk.base64)
    allChunks.push(bytes)
    ensurePlayback()
    if (useBlobFallback) {
      // An HTMLAudioElement source is static, so a live stream can't append progressively: (re)assemble
      // + play only when idle (initial / after end) so a mid-play chunk doesn't restart it. Chunks that
      // arrive while it's playing accumulate for the next (re)assemble — a known manual-smoke limitation.
      if (audioEl && audioEl.paused) {
        switchToBlob()
      }
      return
    }
    appendQueue.push(bytes)
    pumpAppend()
  }

  return { enqueue, reset }
}

// Production singleton.
export const playbackController = createPlaybackController({ store: sessionStore })
