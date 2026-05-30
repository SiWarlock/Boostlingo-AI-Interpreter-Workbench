import { sessionStore } from '../state/sessionStore'
import type { SessionStore } from '../state/sessionStore'
import type { LatencyEvent, UiError } from '../types/domain'
import { realtimeWebRtcClient } from './realtimeWebRtcClient'
import type { RealtimeWebRtcClient } from './realtimeWebRtcClient'

// The realtime connection lifecycle (ARCH-010 §7) — ONE RTCPeerConnection held across turns. Owns connect
// (idempotent — discharges the double-connect guard), the connection-timing stamps, disconnect-surfacing
// (never swallowed, ARCH-018), and teardown on End. DI'd + unit-tested vs the real store + a mocked client;
// the pc/connectionstate ops are the manual-smoke shell. E.5b's mode-switch/recovery reuse teardown + re-mint.

type Clock = () => string

export type RealtimeConnectionManager = {
  ensureConnected: () => Promise<void>
  teardown: () => void
}

export type RealtimeConnectionDeps = {
  store: Pick<SessionStore, 'getState' | 'appendLatencyEvent' | 'failTurn' | 'addError'>
  client: Pick<RealtimeWebRtcClient, 'connect' | 'teardown' | 'onConnectionState'>
  clock: Clock
}

// The frontend coins this code for a connection-state event (not a backend provider error). Generic safe
// message; errorCopy('realtime.session.disconnected') supplies the actionable switch-to-Cascade copy.
const DISCONNECTED: UiError = {
  code: 'realtime.session.disconnected',
  safeMessage: 'The realtime voice connection was lost.',
  // retryable: a fresh turn re-connects (E.5b auto-reconnect); errorCopy advises Cascade as the safer path.
  retryable: true,
  stage: 'realtime',
}

export function createRealtimeConnectionManager(
  deps: RealtimeConnectionDeps,
): RealtimeConnectionManager {
  const { store, client, clock } = deps
  let connectionStarted = false // one pc per session held across turns (idempotent connect)

  function stamp(name: string): void {
    const event: LatencyEvent = {
      name,
      stage: 'realtime',
      timestamp: clock(),
      relativeMs: 0,
      clockSource: 'browser',
      metadata: {},
    }
    store.appendLatencyEvent(event)
  }

  function handleConnectionState(stateName: string): void {
    if (stateName === 'connected') {
      stamp('realtime.session.connected')
    } else if (stateName === 'failed' || stateName === 'disconnected') {
      // Surface the disconnect (ARCH-018, never swallow): stamp it + fail the active turn (populating its
      // errors — the B.9c-ii→E.5 discharge) or, between turns, a session-level error. errorCopy advises
      // switch-to-Cascade. E.5b adds auto-reconnect / re-mint. NOTE: the stamp lands only with an active turn
      // (appendLatencyEvent targets currentTurn); a between-turns disconnect has no turn timeline, so the
      // addError IS the surfaced signal.
      stamp('realtime.session.disconnected')
      if (store.getState().currentTurn !== undefined) {
        store.failTurn(DISCONNECTED)
      } else {
        store.addError(DISCONNECTED)
      }
    }
  }

  async function ensureConnected(): Promise<void> {
    if (connectionStarted) {
      return // one pc held across turns — idempotent (discharges the double-connect orphan-leak guard)
    }
    connectionStarted = true
    client.onConnectionState = (stateName) => handleConnectionState(stateName)
    stamp('realtime.session.connecting') // at INITIATION (before connectionstate fires) → realtime_connect_ms
    try {
      await client.connect()
    } catch (error) {
      connectionStarted = false // a failed connect must NOT latch the guard — let a later turn retry
      throw error // the controller fails + aborts this turn (surfacing the error on it)
    }
  }

  function teardown(): void {
    client.teardown()
    connectionStarted = false // reset so a fresh session re-connects (one pc per session)
  }

  return { ensureConnected, teardown }
}

// Production singleton — wires the real store + client (wall clock).
export const realtimeConnectionManager = createRealtimeConnectionManager({
  store: sessionStore,
  client: realtimeWebRtcClient,
  clock: () => new Date().toISOString(),
})
