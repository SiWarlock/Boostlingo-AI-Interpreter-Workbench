import { describe, expect, it, vi } from 'vitest'
import { createRealtimeTurnController } from './realtimeTurnController'
import { createSessionStore } from '../state/sessionStore'
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
        translationModel: 'gpt-5.4-nano',
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
function setup() {
  const store = createSessionStore()
  store.sessionStarted(realtimeSession()) // sessionId + mode:realtime + direction + active
  const client = {
    connect: vi.fn().mockResolvedValue(undefined),
    sendClientEvent: vi.fn(),
    onServerEvent: null as ((raw: string) => void) | null,
  }
  const api = {
    createTurn: vi.fn().mockResolvedValue({ turnId: 'turn_1' }),
    appendTurnEvents: vi.fn().mockResolvedValue({}),
  }
  const controller = createRealtimeTurnController({ store, client, api, clock: () => FIXED_TS })
  return { store, client, api, controller }
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

  it('startTurn sends session.update(turn_detection:null) then input_audio_buffer.clear and stamps recording.started', async () => {
    const { store, client, controller } = setup()

    await controller.startTurn()

    expect(eventTypes(client)).toEqual(['session.update', 'input_audio_buffer.clear'])
    const sessionUpdate = client.sendClientEvent.mock.calls[0][0] as {
      session: { audio: { input: { turn_detection: unknown } } }
    }
    expect(sessionUpdate.session.audio.input.turn_detection).toBeNull()

    const started = (store.getState().currentTurn?.latencyEvents ?? []).filter(
      (e) => e.name === 'turn.recording.started',
    )
    expect(started).toHaveLength(1)
    expect(started[0]).toMatchObject({ stage: 'overall', clockSource: 'browser', timestamp: FIXED_TS })
  })

  it('connects lazily on the first startTurn and not again when already connected', async () => {
    const { client, api, controller } = setup()
    api.createTurn.mockReset()
    api.createTurn
      .mockResolvedValueOnce({ turnId: 'turn_1' })
      .mockResolvedValueOnce({ turnId: 'turn_2' }) // a genuine 2nd turn, not a duplicate

    await controller.startTurn()
    await controller.startTurn()

    expect(client.connect).toHaveBeenCalledTimes(1)
    expect(api.createTurn).toHaveBeenCalledTimes(2)
  })

  it('routes a server event through parse->normalize->sink into the store', async () => {
    const { store, client, controller } = setup()

    await controller.startTurn()
    client.onServerEvent?.(JSON.stringify({ type: 'response.output_audio_transcript.delta', delta: 'hola' }))

    expect(store.getState().currentTurn?.targetTranscript).toEqual([{ text: 'hola', isFinal: false }])
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
})
