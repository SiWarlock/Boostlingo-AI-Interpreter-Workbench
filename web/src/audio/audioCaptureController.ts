import type { UiError } from '../types/domain'
import { MAX_BLOB_DURATION_MS, micErrorToUiError, probeRecorderMimeType } from './captureSupport'

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

// A manual-stop blob recording (096 push-to-talk). startBlobRecording opens the mic + starts the recorder;
// the caller (the EvaluationPanel) controls the window and calls stop() to end it + receive the blob.
// stop() resolves the captured BlobCapture, or null if the recorder errored. A safety auto-stop bounds the
// window (≤ MAX_BLOB_DURATION_MS) so a forgotten recording can't hold the mic open unbounded.
export type BlobRecordingHandle = { stop: () => Promise<BlobCapture | null> }

export type AudioCaptureController = {
  startStreaming: (handlers: {
    onFrame: CaptureFrameHandler
    onError: CaptureErrorHandler
  }) => Promise<StreamingHandle | null>
  startBlobRecording: () => Promise<BlobRecordingHandle | null>
}

// Injectable browser seam for the G.4 soak harness (decision 1A) — the synthetic MediaStream is supplied
// via `getUserMedia`. Defaults to the browser global, so the production singleton
// (`createAudioCaptureController()`, no args) is byte-identical. Mirrors the realtime client's
// `RealtimeWebRtcDeps.getUserMedia` seam. Streaming-path only; the blob (eval) path stays on the default.
export type AudioCaptureDeps = {
  getUserMedia?: (constraints: MediaStreamConstraints) => Promise<MediaStream>
}

export function createAudioCaptureController(deps: AudioCaptureDeps = {}): AudioCaptureController {
  const getUserMedia =
    deps.getUserMedia ?? ((constraints) => navigator.mediaDevices.getUserMedia(constraints))
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
      stream = await getUserMedia({ audio: true })
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

  async function startBlobRecording(): Promise<BlobRecordingHandle | null> {
    let mediaStream: MediaStream
    try {
      mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true })
    } catch {
      return null // mic denied — the caller surfaces a sanitized capture.failed + resets its UI.
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

    // The recorder settles its blob ONCE — on stop (user Stop OR the safety auto-stop) or on error. stop()
    // returns this same promise so the caller awaits the captured blob regardless of which path stopped it.
    let settle: (value: BlobCapture | null) => void
    const recording = new Promise<BlobCapture | null>((resolve) => {
      settle = resolve
    })
    const release = () => mediaStream.getTracks().forEach((track) => track.stop())
    recorder.onstop = () => {
      release()
      settle({ blob: new Blob(chunks, { type: mimeType }), mimeType })
    }
    // A recorder error must settle the promise (else the caller awaits forever) + release the mic.
    recorder.onerror = () => {
      release()
      settle(null)
    }
    recorder.start()
    // Safety backstop: the user's Stop is the normal path, but a forgotten recording can't hold the mic
    // open unbounded — auto-stop at the max duration (resource guard at the capture boundary).
    const safety = setTimeout(() => {
      if (recorder.state !== 'inactive') recorder.stop()
    }, MAX_BLOB_DURATION_MS)

    return {
      stop: () => {
        clearTimeout(safety)
        if (recorder.state !== 'inactive') recorder.stop()
        return recording
      },
    }
  }

  return { startStreaming, startBlobRecording }
}

// Production singleton — one controller reused across turns (construction touches no device APIs;
// getUserMedia/AudioContext only fire on startStreaming/startBlobRecording).
export const audioCaptureController = createAudioCaptureController()
