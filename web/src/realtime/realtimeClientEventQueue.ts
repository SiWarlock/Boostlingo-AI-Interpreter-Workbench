// The realtime DC-open gate (P0 072). A realtime client event (session.update / input_audio_buffer.clear /
// commit / response.create) sent before the RTCDataChannel reaches readyState 'open' throws
// InvalidStateError — which rejected startTurn and left realtime fully dead. This pure queue is the
// safe-by-construction seam (the realtime analogue of the cascade pre-open frame-queue, web §9/§11):
// buffer client events while the channel is NOT open, flush them IN ORDER on the DC `onopen`. Order matters
// — the session.update (config) must precede later control events. The WebRTC client wires the DC plumbing
// (isOpen / rawSend / onopen→flush / teardown→clear) as a manual-smoke shell; this logic is unit-tested.

export type ClientEventQueue = {
  // Send now if the channel is open, else buffer until flush().
  send: (event: object) => void
  // Drain the buffer in FIFO order (wired to the DC `onopen`).
  flush: () => void
  // Drop any buffered events (wired to teardown — a torn-down session's events must not replay on reconnect).
  clear: () => void
}

export function createClientEventQueue(deps: {
  isOpen: () => boolean
  rawSend: (event: object) => void
}): ClientEventQueue {
  let pending: object[] = []

  return {
    send(event) {
      if (deps.isOpen()) {
        deps.rawSend(event)
      } else {
        pending.push(event)
      }
    },
    flush() {
      const toSend = pending
      pending = []
      for (const event of toSend) {
        deps.rawSend(event)
      }
    },
    clear() {
      pending = []
    },
  }
}
