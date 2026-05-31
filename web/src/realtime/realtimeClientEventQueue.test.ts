import { describe, expect, it, vi } from 'vitest'
import { createClientEventQueue } from './realtimeClientEventQueue'

// The realtime DC-open gate (P0 072). A client event sent before the RTCDataChannel is `open` throws
// InvalidStateError (which rejected startTurn → realtime dead). The queue is the pure, safe-by-construction
// seam: buffer while NOT open, flush in order on the DC `onopen`. Mirrors the cascade pre-open frame-queue
// (web §9/§11) for the realtime control channel. The client's DC wiring is shell (manual-smoke); this is the
// deterministic logic.

function setup(initiallyOpen = false) {
  let open = initiallyOpen
  const rawSend = vi.fn()
  const queue = createClientEventQueue({ isOpen: () => open, rawSend })
  return {
    queue,
    rawSend,
    setOpen: (v: boolean) => {
      open = v
    },
  }
}

describe('createClientEventQueue', () => {
  it('sends immediately when the channel is already open', () => {
    const { queue, rawSend } = setup(true)

    queue.send({ type: 'session.update' })

    expect(rawSend).toHaveBeenCalledWith({ type: 'session.update' })
  })

  it('buffers sends while NOT open (no rawSend) and flushes them IN ORDER on flush() (the P0 fix)', () => {
    const { queue, rawSend, setOpen } = setup(false)

    queue.send({ type: 'session.update' })
    queue.send({ type: 'input_audio_buffer.clear' })
    expect(rawSend).not.toHaveBeenCalled() // nothing reaches dc.send before the channel is open

    setOpen(true)
    queue.flush() // DC onopen
    expect(rawSend).toHaveBeenCalledTimes(2)
    expect(rawSend.mock.calls.map((c) => c[0])).toEqual([
      { type: 'session.update' },
      { type: 'input_audio_buffer.clear' },
    ]) // order preserved (session.update before the buffer clear)
  })

  it('a send AFTER flush (now open) goes straight through, not re-buffered', () => {
    const { queue, rawSend, setOpen } = setup(false)
    queue.send({ type: 'session.update' })
    setOpen(true)
    queue.flush()
    rawSend.mockClear()

    queue.send({ type: 'response.create' })

    expect(rawSend).toHaveBeenCalledWith({ type: 'response.create' })
  })

  it('clear() drops buffered events so a later flush sends nothing (teardown/reconnect reset — no stale leak)', () => {
    const { queue, rawSend, setOpen } = setup(false)
    queue.send({ type: 'session.update' })

    queue.clear() // teardown
    setOpen(true)
    queue.flush() // a fresh connect's onopen must NOT replay the torn-down session's stale events

    expect(rawSend).not.toHaveBeenCalled()
  })
})
