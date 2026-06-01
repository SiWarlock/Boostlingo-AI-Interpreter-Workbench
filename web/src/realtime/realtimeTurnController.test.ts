import { describe, expect, it, vi } from 'vitest'
import { createRealtimeTurnController } from './realtimeTurnController'
import { createSessionStore } from '../state/sessionStore'
import { deriveTurnMetrics } from '../state/selectors'
import type { InterpretationSession } from '../types/domain'

const FIXED_TS = '2026-05-29T12:00:00.000+00:00'

function realtimeSession(): InterpretationSession {
  return {
    sessionId: 'session_abc',
    startedAt: FIXED_TS,
    config: {
      currentMode: 'realtime',
      direction: { source: 'en', target: 'es' },
      providerProfile: {
        realtimeProvider: 'openai',
        realtimeModel: 'gpt-realtime',
        sttProvider: 'deepgram',
        sttModel: 'nova-3',
        sttLanguage: 'multi',
        translationProvider: 'openai',
        translationModel: 'gpt-5-nano',
        ttsProvider: 'openai',
        ttsModel: 'gpt-4o-mini-tts',
        ttsVoice: 'alloy',
      },
    },
    turns: [],
    modeTransitions: [],
    pricingConfigVersion: 'v',
  }
}

// Real store (so the sink->store flow + the responseDone->report-reads-turns path are genuine, web §7/§11),
// mocked client + api. The mock client exposes the surface the controller USES (connect/sendClientEvent +
// a settable onServerEvent delegate); the real client's DC plumbing is manual-smoke.
function setup(clock: () => string = () => FIXED_TS) {
  const store = createSessionStore()
  store.sessionStarted(realtimeSession()) // sessionId + mode:realtime + direction + active
  const client = {
    sendClientEvent: vi.fn(),
    onServerEvent: null as ((raw: string) => void) | null,
  }
  const connectionManager = { ensureConnected: vi.fn().mockResolvedValue(undefined) }
  const api = {
    createTurn: vi.fn().mockResolvedValue({ turnId: 'turn_1' }),
    appendTurnEvents: vi.fn().mockResolvedValue({}),
    completeTurn: vi.fn().mockResolvedValue({}),
  }
  const controller = createRealtimeTurnController({
    store,
    client,
    connectionManager,
    api,
    clock,
  })
  return { store, client, connectionManager, api, controller }
}

// Flush the microtask queue so an async beginAutoSegment (createTurn().then(beginTurn)) settles before the
// next server event is fired. Mirrors the real timing: under server_vad the source emits speech_stopped /
// transcript deltas hundreds of ms AFTER speech_started (>= silence_duration_ms), so the per-segment turn is
// always created before its content arrives. Two ticks cover createTurn's resolve -> .then chain.
async function flush(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
}

// A monotonically increasing ISO clock (stepMs apart) so absolute-timestamp deltas (deriveTurnMetrics) are
// real positive numbers — used to pin the auto-mode speech-end anchor as a non-zero responsiveness value.
function incrementingClock(stepMs = 1000): () => string {
  let t = 0
  return () => {
    const iso = new Date(t).toISOString()
    t += stepMs
    return iso
  }
}

// 076: a sequence clock — returns the given ISO timestamps in order (the last repeats once exhausted). The
// controller's clock() is consumed once per marker() call: #1 = turn.recording.started (in startTurn), #2 =
// turn.recording.stopped (in stopTurn) — so these two control the recording duration deterministically.
function clockSeq(...isos: string[]): () => string {
  let i = 0
  return () => {
    const v = isos[Math.min(i, isos.length - 1)]
    i += 1
    return v
  }
}

function eventTypes(client: ReturnType<typeof setup>['client']): string[] {
  return client.sendClientEvent.mock.calls.map((c) => (c[0] as { type: string }).type)
}

describe('createRealtimeTurnController', () => {
  it('startTurn creates the turn and begins it in realtime mode', async () => {
    const { store, api, controller } = setup()

    await controller.startTurn()

    expect(api.createTurn).toHaveBeenCalledWith('session_abc')
    expect(store.getState().currentTurn).toMatchObject({
      turnId: 'turn_1',
      mode: 'realtime',
      direction: { source: 'en', target: 'es' },
    })
  })

  it('startTurn sends session.update(turn_detection:null + input transcription re-asserted) then input_audio_buffer.clear and stamps recording.started', async () => {
    const { store, client, controller } = setup()

    await controller.startTurn()

    expect(eventTypes(client)).toEqual(['session.update', 'input_audio_buffer.clear'])
    const sessionUpdate = client.sendClientEvent.mock.calls[0][0] as {
      session: {
        type: unknown
        audio: { input: { turn_detection: unknown; transcription: unknown } }
      }
    }
    // GA-required (brief 073): the session.update MUST carry session.type:"realtime" (the session
    // discriminator) or OpenAI rejects the update (missing_required_parameter) → no config applies.
    expect(sessionUpdate.session.type).toBe('realtime')
    expect(sessionUpdate.session.audio.input.turn_detection).toBeNull()
    // Fix B (brief 053): re-assert input transcription in the SAME session.update so this partial
    // audio.input doesn't clobber the mint's input.transcription — else the SOURCE transcript never arrives.
    expect(sessionUpdate.session.audio.input.transcription).toEqual({ model: 'gpt-4o-transcribe' })

    const started = (store.getState().currentTurn?.latencyEvents ?? []).filter(
      (e) => e.name === 'turn.recording.started',
    )
    expect(started).toHaveLength(1)
    expect(started[0]).toMatchObject({
      stage: 'overall',
      clockSource: 'browser',
      timestamp: FIXED_TS,
    })
  })

  it("ensures the connection via the manager on each startTurn (idempotency is the manager's job)", async () => {
    const { connectionManager, api, controller } = setup()
    api.createTurn.mockReset()
    api.createTurn
      .mockResolvedValueOnce({ turnId: 'turn_1' })
      .mockResolvedValueOnce({ turnId: 'turn_2' }) // a genuine 2nd turn, not a duplicate

    await controller.startTurn()
    await controller.startTurn()

    // the controller delegates connect to the manager every turn; the persistent-pc idempotency lives in
    // the manager (its own test), so the controller just ensures-connected per turn.
    expect(connectionManager.ensureConnected).toHaveBeenCalledTimes(2)
    expect(api.createTurn).toHaveBeenCalledTimes(2)
  })

  it('routes a server event through parse->normalize->sink into the store', async () => {
    const { store, client, controller } = setup()

    await controller.startTurn()
    client.onServerEvent?.(
      JSON.stringify({ type: 'response.output_audio_transcript.delta', delta: 'hola' }),
    )

    expect(store.getState().currentTurn?.targetTranscript).toEqual([
      { text: 'hola', isFinal: false },
    ])
  })

  it('stopTurn commits the buffer, requests a response, and stamps recording.stopped', async () => {
    const { store, client, controller } = setup()
    await controller.startTurn()
    client.sendClientEvent.mockClear()

    controller.stopTurn()

    expect(eventTypes(client)).toEqual(['input_audio_buffer.commit', 'response.create'])
    const stopped = (store.getState().currentTurn?.latencyEvents ?? []).filter(
      (e) => e.name === 'turn.recording.stopped',
    )
    expect(stopped).toHaveLength(1)
  })

  it('reports the turn events to the backend on responseDone', async () => {
    const { client, api, controller } = setup()
    await controller.startTurn()

    client.onServerEvent?.(JSON.stringify({ type: 'response.done' }))

    expect(api.appendTurnEvents).toHaveBeenCalledTimes(1)
    const [sid, tid, events] = api.appendTurnEvents.mock.calls[0]
    expect(sid).toBe('session_abc')
    expect(tid).toBe('turn_1')
    // the finalized turn's accumulated client events are reported (incl. the store's turn.completed stamp)
    expect((events as { name: string }[]).some((e) => e.name === 'turn.completed')).toBe(true)
  })

  it('finalizes the turn at /complete with the DC usage token counts + status:completed (053-C2b)', async () => {
    const { client, api, controller } = setup()
    await controller.startTurn()

    client.onServerEvent?.(
      JSON.stringify({
        type: 'response.done',
        response: {
          usage: {
            input_token_details: { audio_tokens: 31, cached_tokens: 0 },
            output_token_details: { audio_tokens: 54 },
          },
        },
      }),
    )

    expect(api.completeTurn).toHaveBeenCalledTimes(1)
    const [sid, tid, body] = api.completeTurn.mock.calls[0]
    expect(sid).toBe('session_abc')
    expect(tid).toBe('turn_1')
    expect(body).toEqual({
      status: 'completed',
      inputAudioTokens: 31,
      outputAudioTokens: 54,
      cachedAudioInputTokens: 0, // real 0 sent (not omitted)
    })
  })

  it('still reports turn events on responseDone (regression — /complete is a sibling, not a replacement)', async () => {
    const { client, api, controller } = setup()
    await controller.startTurn()

    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    expect(api.appendTurnEvents).toHaveBeenCalledTimes(1)
    expect(api.completeTurn).toHaveBeenCalledTimes(1)
  })

  it('honest degrade: usage null → still POSTs /complete to finalize, but WITHOUT token fields', async () => {
    const { client, api, controller } = setup()
    await controller.startTurn()

    client.onServerEvent?.(JSON.stringify({ type: 'response.done' })) // no usage payload → usage null

    expect(api.completeTurn).toHaveBeenCalledTimes(1)
    const [, , body] = api.completeTurn.mock.calls[0]
    expect(body).toEqual({ status: 'completed' }) // no token fields — backend degrades to disclosed-unavailable
  })

  it('surfaces a sanitized realtime.complete_failed when /complete fails (no raw leak)', async () => {
    const { store, client, api, controller } = setup()
    api.completeTurn.mockRejectedValue(new Error('raw-complete-detail'))
    await controller.startTurn()

    client.onServerEvent?.(JSON.stringify({ type: 'response.done' }))
    await Promise.resolve()
    await Promise.resolve()

    const added = store.getState().errors.find((e) => e.code === 'realtime.complete_failed')
    expect(added).toBeDefined()
    expect(added?.safeMessage).not.toContain('raw-complete-detail')
  })

  // 076: the realtime $/min denominator. finalizeTurn sends audioDurationMs (recording/source-speech
  // duration = recording.stopped − recording.started, from the finalized turn's markers) so the backend's
  // existing Build divides cost by it → estimatedUsdPerMinute (today null, blanking the cost comparison).
  it('076: finalize sends the recording duration as audioDurationMs (started → stopped)', async () => {
    const { client, api, controller } = setup(
      clockSeq('2026-05-29T12:00:00.000+00:00', '2026-05-29T12:00:05.000+00:00'),
    )
    await controller.startTurn() // recording.started @ T0
    controller.stopTurn() // recording.stopped @ T0+5000ms

    client.onServerEvent?.(
      JSON.stringify({
        type: 'response.done',
        response: {
          usage: {
            input_token_details: { audio_tokens: 31, cached_tokens: 0 },
            output_token_details: { audio_tokens: 54 },
          },
        },
      }),
    )

    expect(api.completeTurn).toHaveBeenCalledTimes(1)
    const [, , body] = api.completeTurn.mock.calls[0]
    expect(body.audioDurationMs).toBe(5000)
  })

  it('076: omits audioDurationMs when the recording.stopped marker is absent (honest-degrade, never 0)', async () => {
    const { client, api, controller } = setup() // FIXED_TS; NO stopTurn → no recording.stopped marker
    await controller.startTurn()

    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    expect(api.completeTurn).toHaveBeenCalledTimes(1)
    const [, , body] = api.completeTurn.mock.calls[0]
    expect(body).not.toHaveProperty('audioDurationMs') // omitted → backend keeps perMinute null
  })

  it('076: omits audioDurationMs when the duration is not positive (stopped ≤ started clock edge)', async () => {
    const { client, api, controller } = setup(
      clockSeq('2026-05-29T12:00:05.000+00:00', '2026-05-29T12:00:00.000+00:00'), // started LATER than stopped
    )
    await controller.startTurn() // recording.started @ T0+5000
    controller.stopTurn() // recording.stopped @ T0 → duration = −5000 (≤ 0)

    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    const [, , body] = api.completeTurn.mock.calls[0]
    expect(body).not.toHaveProperty('audioDurationMs') // never a 0/negative denominator
  })

  it('076: preserves the token fields + status alongside audioDurationMs (regression — §26)', async () => {
    const { client, api, controller } = setup(
      clockSeq('2026-05-29T12:00:00.000+00:00', '2026-05-29T12:00:03.000+00:00'),
    )
    await controller.startTurn()
    controller.stopTurn()

    client.onServerEvent?.(
      JSON.stringify({
        type: 'response.done',
        response: {
          usage: {
            input_token_details: { audio_tokens: 31, cached_tokens: 0 },
            output_token_details: { audio_tokens: 54 },
          },
        },
      }),
    )

    const [, , body] = api.completeTurn.mock.calls[0]
    expect(body).toEqual({
      status: 'completed',
      inputAudioTokens: 31,
      outputAudioTokens: 54,
      cachedAudioInputTokens: 0,
      audioDurationMs: 3000,
    })
  })

  it('auto mode: Start sends turn_detection server_vad (+ the 053-B transcription re-assert) (I.2 slice 1)', async () => {
    const { store, client, controller } = setup()
    store.setTurnControlMode('auto')

    await controller.startTurn()

    expect(eventTypes(client)).toEqual(['session.update', 'input_audio_buffer.clear'])
    const sessionUpdate = client.sendClientEvent.mock.calls[0][0] as {
      session: {
        type: unknown
        audio: { input: { turn_detection: unknown; transcription: unknown } }
      }
    }
    // GA-required (brief 073): session.type:"realtime" rides on the auto session.update too (both modes
    // were rejected for the missing discriminator).
    expect(sessionUpdate.session.type).toBe('realtime')
    // server-VAD config (GA defaults) — the server now auto-detects speech start/end + auto-creates responses
    expect(sessionUpdate.session.audio.input.turn_detection).toEqual({
      type: 'server_vad',
      threshold: 0.5,
      prefix_padding_ms: 300,
      silence_duration_ms: 500,
    })
    // 053-B: STILL re-assert transcription in the same frame, else the source transcript regresses
    expect(sessionUpdate.session.audio.input.transcription).toEqual({ model: 'gpt-4o-transcribe' })
  })

  it('auto mode: Start opens a listening session (turnStatus recording) WITHOUT creating a turn yet', async () => {
    const { store, api, controller } = setup()
    store.setTurnControlMode('auto')

    await controller.startTurn()

    // The server now owns segmentation: Start opens the mic/listening session (so the Start button disables
    // + Stop enables), but a turn is born only when the server detects a speech segment (speech_started) —
    // NOT eagerly at Start (slice 1's eager createTurn is gone for auto).
    expect(store.getState().turnStatus).toBe('recording')
    expect(store.getState().currentTurn).toBeUndefined()
    expect(api.createTurn).not.toHaveBeenCalled()
  })

  it('auto mode: a 2-segment server-VAD sequence creates 2 turns, each with its own transcript + /complete + /events', async () => {
    const { store, client, api, controller } = setup()
    store.setTurnControlMode('auto')
    api.createTurn.mockReset()
    api.createTurn
      .mockResolvedValueOnce({ turnId: 'turn_1' })
      .mockResolvedValueOnce({ turnId: 'turn_2' })

    await controller.startTurn()

    // --- segment 1 ---
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush() // createTurn(turn_1) -> beginTurn settles before the segment's content arrives
    client.onServerEvent?.(
      JSON.stringify({ type: 'response.output_audio_transcript.delta', delta: 'hola' }),
    )
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_stopped' }))
    client.onServerEvent?.(JSON.stringify({ type: 'output_audio_buffer.started' }))
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    // --- segment 2 (a SECOND utterance in the SAME recording session → its own turn) ---
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush() // createTurn(turn_2) -> beginTurn
    client.onServerEvent?.(
      JSON.stringify({ type: 'response.output_audio_transcript.delta', delta: 'mundo' }),
    )
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_stopped' }))
    client.onServerEvent?.(JSON.stringify({ type: 'output_audio_buffer.started' }))
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    const turns = store.getState().turns
    expect(turns).toHaveLength(2) // one turn PER server-detected segment (not single-utterance-bound)
    expect(api.createTurn).toHaveBeenCalledTimes(2)
    expect(api.completeTurn).toHaveBeenCalledTimes(2) // /complete finalize per segment (§26 per auto-turn)
    expect(api.appendTurnEvents).toHaveBeenCalledTimes(2) // /events per segment
    // each segment's transcript rides into ITS OWN turn (no cross-segment bleed)
    expect(turns[0]).toMatchObject({ turnId: 'turn_1' })
    expect(turns[0].targetTranscript).toEqual([{ text: 'hola', isFinal: true }])
    expect(turns[1]).toMatchObject({ turnId: 'turn_2' })
    expect(turns[1].targetTranscript).toEqual([{ text: 'mundo', isFinal: true }])
  })

  it('auto mode: a single-segment sequence still yields exactly 1 turn (the slice-1 happy path, now via the lifecycle)', async () => {
    const { store, client, api, controller } = setup()
    store.setTurnControlMode('auto')

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush()
    client.onServerEvent?.(
      JSON.stringify({ type: 'response.output_audio_transcript.delta', delta: 'hola' }),
    )
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_stopped' }))
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    expect(store.getState().turns).toHaveLength(1)
    expect(api.completeTurn).toHaveBeenCalledTimes(1)
    expect(api.appendTurnEvents).toHaveBeenCalledTimes(1)
  })

  it('auto mode: the 2nd segment is HANDLED (produces a turn), NOT dropped — slice-1 settled-latch removed', async () => {
    const { store, client, api, controller } = setup()
    store.setTurnControlMode('auto')
    api.createTurn.mockReset()
    api.createTurn
      .mockResolvedValueOnce({ turnId: 'turn_1' })
      .mockResolvedValueOnce({ turnId: 'turn_2' })

    await controller.startTurn()
    // segment 1 fully finalizes
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush()
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))
    // a 2nd utterance — under slice 1 this was DROPPED (settled latch + DEV warn); now it MUST become a turn
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush()
    client.onServerEvent?.(
      JSON.stringify({ type: 'response.output_audio_transcript.delta', delta: 'segunda' }),
    )
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    // the inverse of slice-1's drop-guard: a 2nd turn EXISTS with the 2nd utterance's transcript
    const turns = store.getState().turns
    expect(turns).toHaveLength(2)
    expect(turns[1].targetTranscript).toEqual([{ text: 'segunda', isFinal: true }])
  })

  it('auto mode: between segments the session keeps listening (turnStatus re-armed to recording)', async () => {
    const { store, client, controller } = setup()
    store.setTurnControlMode('auto')

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush()
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} })) // segment 1 finalizes

    // completeTurn flips turnStatus to 'completed' + clears currentTurn; the controller re-arms 'recording'
    // so the hands-free session stays listening (Start stays disabled, Stop stays enabled) for the next
    // segment — instead of the per-turn 'completed' that would re-enable Start mid-session.
    expect(store.getState().turnStatus).toBe('recording')
    expect(store.getState().currentTurn).toBeUndefined()
  })

  it('auto mode: speech_stopped is the speech-end anchor — recording.stopped is stamped from the SERVER signal (no manual Stop), yielding a real responsiveness delta', async () => {
    const { store, client, controller } = setup(incrementingClock())
    store.setTurnControlMode('auto')

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush()
    client.onServerEvent?.(
      JSON.stringify({ type: 'response.output_audio_transcript.delta', delta: 'hola' }),
    )
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_stopped' })) // speech-end
    client.onServerEvent?.(JSON.stringify({ type: 'output_audio_buffer.started' })) // first audio (later)
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    const turn = store.getState().turns[0]
    // stopTurn() was NEVER called (no manual Stop in auto) — yet the segment carries turn.recording.stopped,
    // stamped at the server speech_stopped tick. deriveTurnMetrics anchors responsiveness on it (3A — reuse
    // the marker so the per-turn metric reconciles with the backend session-avg; mirrors cascade §25/§13).
    expect(turn.latencyEvents?.some((e) => e.name === 'turn.recording.stopped')).toBe(true)
    const metrics = deriveTurnMetrics(turn)
    expect(metrics.speechEndToFirstAudioMs).toBeGreaterThan(0) // anchored on server speech-end, not n/a
  })

  it('auto mode: Stop closes the listening session — session.update(turn_detection:null), Start re-enabled, NO commit/response.create', async () => {
    const { store, client, controller } = setup()
    store.setTurnControlMode('auto')
    await controller.startTurn()
    client.sendClientEvent.mockClear()

    controller.stopTurn()

    // Stop in auto = stop the SERVER VAD (turn_detection:null) + return to a startable state — NOT a manual
    // commit/response.create (which would race the server's auto-commit → a double response, slice-1 rule).
    const sessionUpdate = client.sendClientEvent.mock.calls
      .map(
        (c) =>
          c[0] as {
            type: string
            session?: { type?: unknown; audio?: { input?: { turn_detection?: unknown } } }
          },
      )
      .find((e) => e.type === 'session.update')
    // GA-required (brief 073): the close-listening session.update also carries the discriminator.
    expect(sessionUpdate?.session?.type).toBe('realtime')
    expect(sessionUpdate?.session?.audio?.input?.turn_detection).toBeNull()
    expect(eventTypes(client)).not.toContain('input_audio_buffer.commit')
    expect(eventTypes(client)).not.toContain('response.create')
    // the session returns to the startable 'completed' status (canStartRecording true again)
    expect(store.getState().turnStatus).toBe('completed')
  })

  it('auto mode: speech_stopped DURING the createTurn window still anchors the segment (deferred recording.stopped)', async () => {
    const { store, client, api, controller } = setup(incrementingClock())
    store.setTurnControlMode('auto')
    // Hold createTurn unresolved so speech_stopped arrives BEFORE the segment turn exists (the rare race the
    // code-quality review flagged): the speech-end anchor must be deferred + applied when beginTurn settles,
    // not silently dropped.
    let resolveCreate: (v: { turnId: string }) => void = () => {}
    api.createTurn.mockReturnValue(
      new Promise<{ turnId: string }>((resolve) => {
        resolveCreate = resolve
      }),
    )

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    // speech_stopped fires while createTurn is STILL pending (currentSegmentTurnId null, segmentStarting true)
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_stopped' }))
    resolveCreate({ turnId: 'turn_1' }) // NOW the turn is created
    await flush()
    client.onServerEvent?.(JSON.stringify({ type: 'output_audio_buffer.started' }))
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    const turn = store.getState().turns[0]
    expect(turn.latencyEvents?.some((e) => e.name === 'turn.recording.stopped')).toBe(true)
    expect(deriveTurnMetrics(turn).speechEndToFirstAudioMs).toBeGreaterThan(0) // anchor preserved, not n/a
  })

  it('auto mode: a createTurn rejection on speech_started surfaces a sanitized error + keeps listening (segmentStarting resets)', async () => {
    const { store, client, api, controller } = setup()
    store.setTurnControlMode('auto')
    api.createTurn.mockReset()
    api.createTurn
      .mockRejectedValueOnce(new Error('raw-create-detail'))
      .mockResolvedValueOnce({ turnId: 'turn_2' })

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush() // createTurn rejects → .catch addError, .finally resets segmentStarting

    const err = store.getState().errors.find((e) => e.code === 'turn.create_failed')
    expect(err).toBeDefined()
    expect(err?.safeMessage).not.toContain('raw-create-detail') // sanitized, never the raw error
    expect(store.getState().turnStatus).toBe('recording') // the listening session is NOT torn down by one failed segment

    // a SUBSEQUENT speech_started must produce a real turn (segmentStarting was reset → not wedged)
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush()
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))
    expect(store.getState().turns).toHaveLength(1)
    expect(store.getState().turns[0].turnId).toBe('turn_2')
  })

  it('auto mode: a duplicate speech_started DURING an active segment is a no-op (one turn, the guard holds)', async () => {
    const { store, client, api, controller } = setup()
    store.setTurnControlMode('auto')

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush() // segment active (currentSegmentTurnId set)
    // a 2nd speech_started before the active segment's response.done must NOT begin a 2nd turn
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' }))
    await flush()
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    expect(api.createTurn).toHaveBeenCalledTimes(1) // the currentSegmentTurnId guard blocked the duplicate
    expect(store.getState().turns).toHaveLength(1)
  })

  // --- Bug C (070): realtime auto-VAD never auto-finalized live. The 064 lifecycle GATED the whole turn on
  // `speech_started` (an UNCONFIRMED GA string — the 053-C capture is manual, no VAD events). If it doesn't
  // fire, the turn is never begun → response.done can't finalize → the turn never closes. Fix: also begin the
  // segment on the 053-C-CONFIRMED `committed` / `response.created` (guarded → one turn), so the turn exists
  // by response.done regardless of the speech_started shape. (Live auto-finalize = the lead's re-capture.)
  it('auto mode: a segment with NO speech_started STILL finalizes — begins on the confirmed `committed` (Bug C)', async () => {
    const { store, client, api, controller } = setup()
    store.setTurnControlMode('auto')

    await controller.startTurn()
    // server-VAD fired but speech_started did NOT reach the controller (wrong/absent GA string) — `committed`
    // (053-C-confirmed) is the fallback begin-trigger so the turn exists before response.done.
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.committed' }))
    await flush() // createTurn resolves → the segment turn exists
    client.onServerEvent?.(
      JSON.stringify({ type: 'response.output_audio_transcript.delta', delta: 'hola' }),
    )
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    expect(api.createTurn).toHaveBeenCalledTimes(1)
    expect(api.completeTurn).toHaveBeenCalledTimes(1) // the turn FINALIZED (it no longer hangs open)
    expect(store.getState().turns).toHaveLength(1)
    expect(store.getState().turns[0].targetTranscript).toEqual([{ text: 'hola', isFinal: true }])
  })

  it('auto mode: a segment with ONLY response.created (no speech_started, no committed) still finalizes (last-resort begin)', async () => {
    const { store, client, api, controller } = setup()
    store.setTurnControlMode('auto')

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'response.created' })) // belt-and-suspenders begin-trigger
    await flush()
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    expect(api.createTurn).toHaveBeenCalledTimes(1)
    expect(api.completeTurn).toHaveBeenCalledTimes(1)
    expect(store.getState().turns).toHaveLength(1)
  })

  it('auto mode: speech_started + committed + response.created all fire → EXACTLY ONE turn (idempotent begin)', async () => {
    const { store, client, api, controller } = setup()
    store.setTurnControlMode('auto')

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_started' })) // begins
    await flush()
    // the real GA sequence ALSO emits these after speech_started — they must NOT begin a 2nd turn
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.committed' }))
    client.onServerEvent?.(JSON.stringify({ type: 'response.created' }))
    await flush()
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    expect(api.createTurn).toHaveBeenCalledTimes(1) // the guard collapses all three triggers to one begin
    expect(store.getState().turns).toHaveLength(1)
  })

  it('auto mode: the speech-end anchor survives the committed-begin path (speech_stopped before the turn → deferred + applied)', async () => {
    const { store, client, controller } = setup(incrementingClock())
    store.setTurnControlMode('auto')

    await controller.startTurn()
    // speech_stopped arrives BEFORE any begin-trigger (speech_started absent) — capture the speech-end time
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.speech_stopped' }))
    client.onServerEvent?.(JSON.stringify({ type: 'input_audio_buffer.committed' })) // begins the segment
    await flush() // turn created → the deferred speech-end anchor is applied
    client.onServerEvent?.(JSON.stringify({ type: 'output_audio_buffer.started' })) // first audio (later)
    client.onServerEvent?.(JSON.stringify({ type: 'response.done', response: {} }))

    const turn = store.getState().turns[0]
    expect(turn.latencyEvents?.some((e) => e.name === 'turn.recording.stopped')).toBe(true)
    expect(deriveTurnMetrics(turn).speechEndToFirstAudioMs).toBeGreaterThan(0) // anchor preserved, not n/a
  })

  it('guards a concurrent double startTurn — only one turn is created', async () => {
    const { api, controller } = setup()

    await Promise.all([controller.startTurn(), controller.startTurn()])

    expect(api.createTurn).toHaveBeenCalledTimes(1)
  })

  it('surfaces a sanitized realtime.report_failed when reporting fails on responseDone (no raw leak)', async () => {
    const { store, client, api, controller } = setup()
    api.appendTurnEvents.mockRejectedValue(new Error('raw-network-detail'))
    await controller.startTurn()

    client.onServerEvent?.(JSON.stringify({ type: 'response.done' }))
    // the report is fire-and-forget — flush the rejection's .catch microtask(s).
    await Promise.resolve()
    await Promise.resolve()

    const added = store.getState().errors.find((e) => e.code === 'realtime.report_failed')
    expect(added).toBeDefined()
    expect(added?.safeMessage).not.toContain('raw-network-detail') // fixed generic, never the raw error
  })

  it('fails + aborts the turn when the connection cannot be established (no raw leak)', async () => {
    const { store, client, connectionManager, controller } = setup()
    connectionManager.ensureConnected.mockRejectedValue(new Error('connect boom'))

    await controller.startTurn()

    const turn = store.getState().currentTurn
    expect(turn?.status).toBe('failed')
    expect(turn?.errors.some((e) => e.code === 'realtime.connect')).toBe(true)
    expect(turn?.errors[0].safeMessage).not.toContain('connect boom') // fixed generic
    // aborted before any control frame / recording stamp went out
    expect(client.sendClientEvent).not.toHaveBeenCalled()
  })
})
