import type { UiError } from '../types/domain'
import { clampBlobDurationMs, micErrorToUiError, probeRecorderMimeType } from './captureSupport'

// The audio capture controller (ARCH-030 / ARCH-007). MANUAL-SMOKE shell (ARCH-020): the
// getUserMedia / AudioContext / AudioWorkletNode / MediaRecorder wiring is browser-realm and exercised
// in the C/D demo checklist; the deterministic cores it relies on (linear16 conversion in pcm.ts, the
// mimeType probe + mic-error mapping in captureSupport.ts) are unit-tested.
//
// Clean separation (ARCH-007): emits frames/errors via callbacks; imports NEITHER the store NOR any
// transport client — so the realtime path (E) can reuse the same getUserMedia capture. D.4 wires
// onFrame -> cascadeStreamClient and onError -> the store.

export type CaptureFrameHandler = (frame: ArrayBuffer) => void
export type CaptureErrorHandler = (error: UiError) => void

export type StreamingHandle = {
  sampleRate: number
  encoding: 'linear16'
  stop: () => void
}

export type BlobCapture = { blob: Blob; mimeType: string }

export type AudioCaptureController = {
  startStreaming: (handlers: {
    onFrame: CaptureFrameHandler
    onError: CaptureErrorHandler
  }) => Promise<StreamingHandle | null>
  recordBlob: (durationMs?: number) => Promise<BlobCapture | null>
}

export function createAudioCaptureController(): AudioCaptureController {
  let context: AudioContext | null = null
  let stream: MediaStream | null = null

  function stop(): void {
    stream?.getTracks().forEach((track) => track.stop())
    void context?.close()
    context = null
    stream = null
  }

  async function startStreaming(handlers: {
    onFrame: CaptureFrameHandler
    onError: CaptureErrorHandler
  }): Promise<StreamingHandle | null> {
    // Single-use contract: start once, stop(), then start again (stop() nulls these). Guard against a
    // re-entrant start that would orphan the prior stream/context.
    if (stream !== null || context !== null) {
      return null
    }
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: true })
      context = new AudioContext()
      await context.audioWorklet.addModule(new URL('./pcmWorklet.ts', import.meta.url))
      const source = context.createMediaStreamSource(stream)
      const node = new AudioWorkletNode(context, 'pcm-frame-processor')
      node.port.onmessage = (event: MessageEvent<ArrayBuffer>) => handlers.onFrame(event.data)
      source.connect(node)
      // Connect to the destination so the worklet's process() is pulled by the render graph. The
      // worklet writes no output (silence), so this routes no audible mic feedback to the speakers.
      node.connect(context.destination)
    } catch (error) {
      // Any setup failure (mic denied, worklet load) -> tear down + surface a sanitized error.
      stop()
      handlers.onError(micErrorToUiError(error))
      return null
    }

    return {
      // The ACTUAL context rate is declared (no resample) so the backend STT uses the true rate.
      sampleRate: context.sampleRate,
      encoding: 'linear16',
      stop,
    }
  }

  async function recordBlob(durationMs?: number): Promise<BlobCapture | null> {
    let mediaStream: MediaStream
    try {
      mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true })
    } catch {
      return null
    }

    const mimeType = probeRecorderMimeType((type) => MediaRecorder.isTypeSupported(type))
    if (mimeType === null) {
      mediaStream.getTracks().forEach((track) => track.stop())
      return null
    }

    const recorder = new MediaRecorder(mediaStream, { mimeType })
    const chunks: BlobPart[] = []
    recorder.ondataavailable = (event: BlobEvent) => {
      if (event.data.size > 0) chunks.push(event.data)
    }

    return await new Promise<BlobCapture | null>((resolve) => {
      const release = () => mediaStream.getTracks().forEach((track) => track.stop())
      recorder.onstop = () => {
        release()
        resolve({ blob: new Blob(chunks, { type: mimeType }), mimeType })
      }
      // A recorder error must settle the promise (else the caller awaits forever) + release the mic.
      recorder.onerror = () => {
        release()
        resolve(null)
      }
      recorder.start()
      // Bounded auto-stop (clamped) so a stray/huge duration can't hold the mic open unbounded.
      setTimeout(() => recorder.stop(), clampBlobDurationMs(durationMs ?? Number.NaN))
    })
  }

  return { startStreaming, recordBlob }
}

// Production singleton — one controller reused across turns (construction touches no device APIs;
// getUserMedia/AudioContext only fire on startStreaming/recordBlob).
export const audioCaptureController = createAudioCaptureController()
