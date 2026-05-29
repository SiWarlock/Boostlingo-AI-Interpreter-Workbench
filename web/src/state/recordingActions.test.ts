import { describe, expect, it, vi } from 'vitest'
import { createRecordingController } from './recordingActions'
import { ApiError } from '../api/http'
import type { UiSessionState } from '../types/domain'

function baseState(overrides: Partial<UiSessionState> = {}): UiSessionState {
  return {
    sessionId: 'session_abc',
    mode: 'cascade',
    direction: { source: 'en', target: 'es' },
    realtimeModel: 'gpt-realtime',
    translationModel: 'gpt-5.4-nano',
    sessionStatus: 'active',
    turnStatus: 'ready',
    turns: [],
    errors: [],
    ...overrides,
  }
}

function setup(state: UiSessionState = baseState()) {
  const captureStop = vi.fn()
  const store = {
    getState: vi.fn(() => state),
    beginTurn: vi.fn(),
    failTurn: vi.fn(),
    addError: vi.fn(),
    setTurnStatus: vi.fn(),
  }
  const deps = {
    store,
    createTurn: vi.fn().mockResolvedValue({ turnId: 'turn_001' }),
    client: { start: vi.fn(), sendFrame: vi.fn(), stop: vi.fn() },
    capture: {
      startStreaming: vi
        .fn()
        .mockResolvedValue({ sampleRate: 48000, encoding: 'linear16' as const, stop: captureStop }),
    },
  }
  return { deps, store, captureStop }
}

describe('startRecording', () => {
  it('creates the turn, begins it, starts capture, opens the WS, and pipes frames', async () => {
    const { deps, store } = setup()
    const controller = createRecordingController(deps)

    await controller.startRecording()

    expect(deps.createTurn).toHaveBeenCalledWith('session_abc')
    expect(store.beginTurn).toHaveBeenCalledWith({
      turnId: 'turn_001',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })
    expect(deps.capture.startStreaming).toHaveBeenCalledTimes(1)
    expect(deps.client.start).toHaveBeenCalledWith(
      expect.objectContaining({
        sessionId: 'session_abc',
        turnId: 'turn_001',
        direction: { source: 'en', target: 'es' },
        sampleRate: 48000, // from the capture handle
        translationModel: 'gpt-5.4-nano',
        ttsVoice: '', // blank -> the backend ResolveVoice picks the per-target-language voice
      }),
    )

    // ordering: createTurn -> beginTurn -> startStreaming -> client.start
    expect(deps.createTurn.mock.invocationCallOrder[0]).toBeLessThan(
      store.beginTurn.mock.invocationCallOrder[0],
    )
    expect(store.beginTurn.mock.invocationCallOrder[0]).toBeLessThan(
      deps.capture.startStreaming.mock.invocationCallOrder[0],
    )
    expect(deps.capture.startStreaming.mock.invocationCallOrder[0]).toBeLessThan(
      deps.client.start.mock.invocationCallOrder[0],
    )

    // the onFrame wired into capture forwards to the client
    const handlers = deps.capture.startStreaming.mock.calls[0][0]
    const buf = new ArrayBuffer(4)
    handlers.onFrame(buf)
    expect(deps.client.sendFrame).toHaveBeenCalledWith(buf)
    // and onError fails the turn
    handlers.onError({ code: 'mic.permission_denied', safeMessage: 'x', retryable: true })
    expect(store.failTurn).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'mic.permission_denied' }),
    )
  })

  it('aborts cleanly on a createTurn ApiError — no orphaned capture/WS', async () => {
    const { deps, store } = setup()
    deps.createTurn.mockRejectedValue(
      new ApiError({ code: 'rate_limited', safeMessage: 'Too many requests.', retryable: true }),
    )
    const controller = createRecordingController(deps)

    await controller.startRecording()

    expect(store.addError).toHaveBeenCalledWith(expect.objectContaining({ code: 'rate_limited' }))
    expect(store.beginTurn).not.toHaveBeenCalled()
    expect(deps.capture.startStreaming).not.toHaveBeenCalled()
    expect(deps.client.start).not.toHaveBeenCalled()
  })

  it('does nothing when there is no active session', async () => {
    const { deps, store } = setup(baseState({ sessionId: null }))
    const controller = createRecordingController(deps)

    await controller.startRecording()

    expect(deps.createTurn).not.toHaveBeenCalled()
    expect(store.beginTurn).not.toHaveBeenCalled()
  })

  it('guards against a concurrent double-start — only one turn is created', async () => {
    const { deps } = setup()
    const controller = createRecordingController(deps)

    await Promise.all([controller.startRecording(), controller.startRecording()])

    expect(deps.createTurn).toHaveBeenCalledTimes(1) // the re-entrant call is dropped
  })

  it('on a non-ApiError createTurn failure surfaces a fixed error without leaking', async () => {
    const { deps, store } = setup()
    deps.createTurn.mockRejectedValue(new Error('raw-network-detail'))
    const controller = createRecordingController(deps)

    await controller.startRecording()

    expect(store.addError).toHaveBeenCalledWith(
      expect.objectContaining({ code: 'turn.create_failed' }),
    )
    const added = store.addError.mock.calls[0][0]
    expect(added.safeMessage).not.toContain('raw-network-detail')
    expect(store.beginTurn).not.toHaveBeenCalled()
  })
})

describe('stopRecording', () => {
  it('stops capture + the socket and moves the turn to processing', async () => {
    const { deps, store, captureStop } = setup()
    const controller = createRecordingController(deps)
    await controller.startRecording() // sets the capture handle

    controller.stopRecording()

    expect(captureStop).toHaveBeenCalledTimes(1)
    expect(deps.client.stop).toHaveBeenCalledTimes(1)
    expect(store.setTurnStatus).toHaveBeenCalledWith('processing')
  })

  it('is null-safe when called without a prior start (no capture handle)', () => {
    const { deps, store } = setup()
    const controller = createRecordingController(deps)

    expect(() => controller.stopRecording()).not.toThrow() // captureHandle?.stop() is null-safe
    expect(deps.client.stop).toHaveBeenCalledTimes(1)
    expect(store.setTurnStatus).toHaveBeenCalledWith('processing')
  })
})
