import { describe, expect, it, vi } from 'vitest'
import { createRealtimeConnectionManager } from './realtimeConnectionManager'
import { createSessionStore } from '../state/sessionStore'
import type { SessionStore } from '../state/sessionStore'

const FIXED_TS = '2026-05-29T12:00:00.000+00:00'

// Real store (so disconnect→failTurn populates the turn's errors genuinely), mocked client. The client
// exposes the surface the manager USES (connect/teardown + a settable onConnectionState delegate); the real
// pc/connectionstate plumbing is manual-smoke.
function setup(withTurn = true) {
  const store = createSessionStore()
  if (withTurn) {
    store.beginTurn({ turnId: 't1', mode: 'realtime', direction: { source: 'en', target: 'es' } })
  }
  const client = {
    connect: vi.fn().mockResolvedValue(undefined),
    teardown: vi.fn(),
    onConnectionState: null as ((state: string) => void) | null,
  }
  const manager = createRealtimeConnectionManager({ store, client, clock: () => FIXED_TS })
  return { store, client, manager }
}

function stamps(store: SessionStore, name: string) {
  return (store.getState().currentTurn?.latencyEvents ?? []).filter((e) => e.name === name)
}

describe('createRealtimeConnectionManager', () => {
  it('stamps realtime.session.connecting at connect INITIATION (before connectionstate fires)', async () => {
    const { store, manager } = setup()

    await manager.ensureConnected()

    const connecting = stamps(store, 'realtime.session.connecting')
    expect(connecting).toHaveLength(1)
    expect(connecting[0]).toMatchObject({ stage: 'realtime', clockSource: 'browser', timestamp: FIXED_TS })
    // connected has NOT been stamped yet — that waits for the pc connectionstate event
    expect(stamps(store, 'realtime.session.connected')).toHaveLength(0)
  })

  it('stamps realtime.session.connected on the connectionstate `connected` event', async () => {
    const { store, client, manager } = setup()
    await manager.ensureConnected()

    client.onConnectionState?.('connected')

    expect(stamps(store, 'realtime.session.connected')).toHaveLength(1)
  })

  it('holds one connection across turns — a 2nd ensureConnected is idempotent (no 2nd connect)', async () => {
    const { client, manager } = setup()

    await manager.ensureConnected()
    await manager.ensureConnected()

    expect(client.connect).toHaveBeenCalledTimes(1)
  })

  it('surfaces a connectionstate `failed` as a sanitized disconnect that fails the active turn (no leak)', async () => {
    const { store, client, manager } = setup()
    await manager.ensureConnected()

    client.onConnectionState?.('failed')

    expect(stamps(store, 'realtime.session.disconnected')).toHaveLength(1)
    const turn = store.getState().currentTurn
    expect(turn?.status).toBe('failed')
    const err = turn?.errors[turn.errors.length - 1]
    expect(err).toMatchObject({ code: 'realtime.session.disconnected', stage: 'realtime' })
    expect(err?.safeMessage).not.toContain('failed') // fixed-generic, never raw connectionstate text
  })

  it('surfaces a `disconnected` between turns as a session-level error (addError, no active turn)', async () => {
    const { store, client, manager } = setup(false) // no active turn
    await manager.ensureConnected()

    client.onConnectionState?.('disconnected')

    const added = store.getState().errors.find((e) => e.code === 'realtime.session.disconnected')
    expect(added).toMatchObject({ code: 'realtime.session.disconnected', stage: 'realtime' })
    expect(store.getState().currentTurn).toBeUndefined()
    // Between turns there is no turn timeline, so the disconnect STAMP is intentionally dropped (the store's
    // appendLatencyEvent targets currentTurn); the addError above is the surfaced signal.
  })

  it('does NOT latch the connection on a failed connect — a later ensureConnected can reconnect', async () => {
    const { client, manager } = setup()
    client.connect.mockReset()
    client.connect.mockRejectedValueOnce(new Error('connect boom')).mockResolvedValueOnce(undefined)

    await expect(manager.ensureConnected()).rejects.toThrow() // first attempt fails
    await manager.ensureConnected() // retry succeeds because the latch was reset

    expect(client.connect).toHaveBeenCalledTimes(2)
  })

  it('teardown tears down the client and resets so a fresh session re-connects', async () => {
    const { client, manager } = setup()
    await manager.ensureConnected()

    manager.teardown()

    expect(client.teardown).toHaveBeenCalledTimes(1)
    // the connected latch is reset — a subsequent ensureConnected re-connects (one pc per session)
    await manager.ensureConnected()
    expect(client.connect).toHaveBeenCalledTimes(2)
  })

  it('tears down the realtime connection on a realtime->cascade switch-away (Flow G, no double-mic)', () => {
    const { client, manager } = setup(false)

    manager.onModeSwitch('realtime', 'cascade')

    expect(client.teardown).toHaveBeenCalledTimes(1)
  })

  it('does NOT tear down on a cascade->realtime switch (realtime reconnects lazily next turn)', () => {
    const { client, manager } = setup(false)

    manager.onModeSwitch('cascade', 'realtime')

    expect(client.teardown).not.toHaveBeenCalled()
  })

  it('does NOT tear down when the mode does not switch away from realtime (no-op / same mode)', () => {
    const { client, manager } = setup(false)

    manager.onModeSwitch('cascade', 'cascade')
    manager.onModeSwitch('realtime', 'realtime')

    expect(client.teardown).not.toHaveBeenCalled()
  })
})
