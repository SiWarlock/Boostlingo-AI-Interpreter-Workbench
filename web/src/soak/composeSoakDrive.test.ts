import { describe, expect, it } from 'vitest'
import { buildSoakScheduleFromBuffers } from './composeSoakDrive'

// `composeSoakDrive` is mostly SMOKE (real browser audio + real capture/realtime clients + the real drive)
// — validated at the manual real-key run. Its one deterministic, correctness-critical bit is index-aligning
// the decoded buffer durations to the 087 schedule (a wrong alignment desyncs the whole conversation). That
// helper is TDD'd here.
describe('buildSoakScheduleFromBuffers', () => {
  it('builds an index-aligned schedule from decoded buffer durations (seconds → ms) + the gap', () => {
    const buffers = [{ duration: 1.0 }, { duration: 1.5 }] as unknown as AudioBuffer[]

    const schedule = buildSoakScheduleFromBuffers(buffers, 500)

    // AudioBuffer.duration is SECONDS → ms; durations 1000/1500 + gap 500 → offsets 0, 1500.
    expect(schedule.utterances.map((u) => u.durationMs)).toEqual([1000, 1500])
    expect(schedule.utterances.map((u) => u.startOffsetMs)).toEqual([0, 1500])
  })
})
