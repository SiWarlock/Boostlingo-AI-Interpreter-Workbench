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
    translationModel: 'gpt-5-nano',
    sessionStatus: 'active',
    turnStatus: 'ready',
    turnControlMode: 'manual',
    bidirectional: false,
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
    appendLatencyEvent: vi.fn(),
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
  it('passes bidirectional: true to the cascade start frame when enabled (J.3); omits it when off', async () => {
    // Phase J: the store-level bidirectional flag flows into the cascade WS start frame so the backend
    // (cascade-078) auto-detects + flips direction per utterance. Omit-when-off keeps the one-direction
    // start frame byte-identical (buildStartFrame drops an absent key).
    const on = setup(baseState({ bidirectional: true }))
    await createRecordingController(on.deps).startRecording()
    expect(on.deps.client.start).toHaveBeenCalledWith(
      expect.objectContaining({ bidirectional: true }),
    )

    const off = setup() // bidirectional: false
    await createRecordingController(off.deps).startRecording()
    const startArg = off.deps.client.start.mock.calls[0][0] as Record<string, unknown>
    expect('bidirectional' in startArg).toBe(false)
  })

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
        translationModel: 'gpt-5-nano',
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

// --- I.3: cascade auto-VAD wiring (the realtime 063-s1 analogue) ------------------------------------
// The turnControlMode toggle (063-s1) drives the cascade start frame's autoVad; in auto mode the backend
// auto-finalizes on Deepgram utterance-end -> `done` -> completeTurn finalizes the TURN. But no frontend
// Stop fires, so recordingActions must stop the CAPTURE on the auto-finalize (onCascadeTerminal), idempotent
// with a manual Stop (the early-end). Single-utterance (062 finalizes the whole turn on the first end).
describe('cascade auto-VAD (I.3)', () => {
  it('auto mode: startRecording sends autoVad:true in the cascade start frame', async () => {
    const { deps } = setup(baseState({ turnControlMode: 'auto' }))
    const controller = createRecordingController(deps)

    await controller.startRecording()

    expect(deps.client.start).toHaveBeenCalledWith(expect.objectContaining({ autoVad: true }))
  })

  it('manual mode: startRecording OMITS autoVad entirely (manual wire byte-identical to pre-062)', async () => {
    const { deps } = setup() // default turnControlMode 'manual'
    const controller = createRecordingController(deps)

    await controller.startRecording()

    // the key is absent, not present-false → buildStartFrame's exact-frame contract stays intact
    expect(deps.client.start.mock.calls[0][0]).not.toHaveProperty('autoVad')
  })

  it('onCascadeTerminal stops + clears the capture (the auto-finalize: no manual Stop fires) and is idempotent', async () => {
    const { deps, captureStop } = setup(baseState({ turnControlMode: 'auto' }))
    const controller = createRecordingController(deps)
    await controller.startRecording() // sets the capture handle

    controller.onCascadeTerminal()
    expect(captureStop).toHaveBeenCalledTimes(1) // the mic stops on the backend auto-finalize
    // a 2nd terminal (or a late manual Stop) does NOT re-stop the now-cleared handle
    controller.onCascadeTerminal()
    expect(captureStop).toHaveBeenCalledTimes(1)
  })

  it('idempotent with a manual Stop early-end: stopRecording then the backend done→onCascadeTerminal stops capture ONCE', async () => {
    const { deps, captureStop } = setup(baseState({ turnControlMode: 'auto' }))
    const controller = createRecordingController(deps)
    await controller.startRecording()

    controller.stopRecording() // early-end in auto: client.stop() + stops capture
    controller.onCascadeTerminal() // the backend's `done` arrives afterward

    expect(captureStop).toHaveBeenCalledTimes(1) // not double-stopped
    expect(deps.client.stop).toHaveBeenCalledTimes(1) // the early-end stop
  })

  it('onCascadeTerminal is null-safe with no prior start (no throw)', () => {
    const { deps, captureStop } = setup()
    const controller = createRecordingController(deps)

    expect(() => controller.onCascadeTerminal()).not.toThrow()
    expect(captureStop).not.toHaveBeenCalled()
  })
})

// --- D.6: the client-clock recording markers the top-level latency deltas need ----------------
// turn.recording.started / turn.recording.stopped are browser-clock LatencyEvents stamped via the
// store; deriveTurnMetrics reads their absolute timestamps to compute speechEnd→* + totalTurn.
describe('recording stamps (top-level latency markers)', () => {
  function stampNamed(store: ReturnType<typeof setup>['store'], name: string) {
    return store.appendLatencyEvent.mock.calls.map((c) => c[0]).find((e) => e.name === name)
  }

  it('startRecording stamps turn.recording.started (browser clock, overall) once capture starts', async () => {
    const { deps, store } = setup()
    const controller = createRecordingController(deps)

    await controller.startRecording()

    const ev = stampNamed(store, 'turn.recording.started')
    expect(ev).toMatchObject({
      name: 'turn.recording.started',
      stage: 'overall',
      clockSource: 'browser',
    })
    expect(typeof ev?.timestamp).toBe('string')
    expect(Number.isNaN(Date.parse(ev!.timestamp))).toBe(false) // a real ISO timestamp, not a placeholder
  })

  it('does NOT stamp turn.recording.started when the mic is denied (capture returns null)', async () => {
    const { deps, store } = setup()
    deps.capture.startStreaming.mockResolvedValue(null) // mic-denied / capture-failed → no audio captured
    const controller = createRecordingController(deps)

    await controller.startRecording()

    expect(stampNamed(store, 'turn.recording.started')).toBeUndefined()
  })

  it('stopRecording stamps turn.recording.stopped (browser clock, overall) when stop is sent', async () => {
    const { deps, store } = setup()
    const controller = createRecordingController(deps)
    await controller.startRecording()

    controller.stopRecording()

    const ev = stampNamed(store, 'turn.recording.stopped')
    expect(ev).toMatchObject({
      name: 'turn.recording.stopped',
      stage: 'overall',
      clockSource: 'browser',
    })
    expect(Number.isNaN(Date.parse(ev!.timestamp))).toBe(false)
  })
})
