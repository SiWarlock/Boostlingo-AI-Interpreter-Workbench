import { describe, expect, it, vi } from 'vitest'
import { createSyntheticAudioStream } from './syntheticAudioStream'
import type { SyntheticStreamContext } from './syntheticAudioStream'
import { computeSchedule } from './soakSchedule'

// The synthetic-stream generator (G.4 / decision 1A + Q4): on a harness-owned AudioContext, decoded TTS
// buffers → AudioBufferSourceNodes → one MediaStreamAudioDestinationNode → `.stream` (the single MediaStream
// injected at each mode's getUserMedia). The browser-audio nodes are manual-smoke; the SCHEDULE it consumes
// (087 `computeSchedule`) is the TDD'd seam — each source must start at its 1×-real-time offset.
describe('createSyntheticAudioStream', () => {
  it('schedules each decoded buffer at its 1x-real-time offset on the harness AudioContext', () => {
    const buffers = [{ duration: 1.0 }, { duration: 1.5 }] as unknown as AudioBuffer[]
    const schedule = computeSchedule([1000, 1500], 500) // start offsets 0 ms, 1500 ms

    const startTimes: number[] = []
    const made: Array<{ buffer: AudioBuffer | null }> = []
    const makeSource = () => {
      const source = {
        buffer: null as AudioBuffer | null,
        connect: vi.fn(),
        start: vi.fn((when: number) => startTimes.push(when)),
        stop: vi.fn(),
      }
      made.push(source)
      return source
    }
    const destination = { stream: { id: 'synthetic' } }
    const context = {
      currentTime: 100,
      createBufferSource: vi.fn(() => makeSource()),
      createMediaStreamDestination: vi.fn(() => destination),
    } as unknown as SyntheticStreamContext

    const synthetic = createSyntheticAudioStream({ context, buffers, schedule })
    expect(synthetic.stream).toBe(destination.stream)

    synthetic.start()

    // currentTime (100 s) + each offsetMs/1000 → [100.0, 101.5]; buffers wired in order.
    expect(startTimes).toEqual([100, 101.5])
    expect(made[0].buffer).toBe(buffers[0])
    expect(made[1].buffer).toBe(buffers[1])
  })
})
