import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAudioCaptureController } from './audioCaptureController'

// The `getUserMedia` DI seam (G.4 / decision 1A) — cascade parity with the realtime client's existing
// `RealtimeWebRtcDeps.getUserMedia`. The browser-audio graph (AudioContext / worklet) is manual-smoke-exempt
// (ARCH-020 / ARCH-030); here we stub it just enough to drive `startStreaming` past `getUserMedia` and assert
// WHICH getUserMedia it used — the injected one (the synthetic-stream injection point) vs the navigator
// default (the production singleton, unchanged). The injected synthetic MediaStream must reach the capture
// graph (`createMediaStreamSource`).

function stubBrowserAudio(): { createMediaStreamSource: ReturnType<typeof vi.fn> } {
  const createMediaStreamSource = vi.fn(() => ({ connect: vi.fn() }))
  class FakeAudioContext {
    sampleRate = 48000
    destination = {}
    audioWorklet = { addModule: vi.fn().mockResolvedValue(undefined) }
    createMediaStreamSource = createMediaStreamSource
    close = vi.fn()
  }
  class FakeAudioWorkletNode {
    port: { onmessage: ((event: MessageEvent) => void) | null } = { onmessage: null }
    connect = vi.fn()
  }
  vi.stubGlobal('AudioContext', FakeAudioContext)
  vi.stubGlobal('AudioWorkletNode', FakeAudioWorkletNode)
  return { createMediaStreamSource }
}

const fakeStream = (): MediaStream => ({ getTracks: () => [] }) as unknown as MediaStream

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('createAudioCaptureController — getUserMedia DI seam', () => {
  it('uses the injected getUserMedia (the synthetic-stream injection point)', async () => {
    const { createMediaStreamSource } = stubBrowserAudio()
    const stream = fakeStream()
    const getUserMedia = vi.fn().mockResolvedValue(stream)
    const controller = createAudioCaptureController({ getUserMedia })

    const handle = await controller.startStreaming({ onFrame: vi.fn(), onError: vi.fn() })

    expect(getUserMedia).toHaveBeenCalledWith({ audio: true })
    expect(createMediaStreamSource).toHaveBeenCalledWith(stream) // injected stream reaches the worklet graph
    expect(handle).not.toBeNull()
    expect(handle?.encoding).toBe('linear16')
  })

  it('defaults to navigator.mediaDevices.getUserMedia when no dep is given (production singleton unchanged)', async () => {
    stubBrowserAudio()
    const navGetUserMedia = vi.fn().mockResolvedValue(fakeStream())
    vi.stubGlobal('navigator', { mediaDevices: { getUserMedia: navGetUserMedia } })
    const controller = createAudioCaptureController()

    const handle = await controller.startStreaming({ onFrame: vi.fn(), onError: vi.fn() })

    expect(navGetUserMedia).toHaveBeenCalledWith({ audio: true })
    expect(handle).not.toBeNull()
  })
})
