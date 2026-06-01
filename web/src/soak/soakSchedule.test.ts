import { describe, expect, it } from 'vitest'
import { computeSchedule } from './soakSchedule'

// 1×-real-time pacing constraint (G.4 design "Design constraints"): each utterance is injected at its
// cumulative scripted offset so the 5-min soak genuinely takes 5 min. Given runtime-decoded durations
// (ms) + a configured inter-utterance gap, the schedule is the cumulative start offsets + expected
// playback-end times — the deterministic timeline the overlap detector reads against.
describe('computeSchedule', () => {
  it('lays out cumulative start offsets + expected ends at 1x real-time', () => {
    const schedule = computeSchedule([1000, 1500], 500)
    expect(schedule.utterances).toEqual([
      { index: 0, startOffsetMs: 0, durationMs: 1000, expectedEndMs: 1000 },
      // utt1 starts after utt0's duration (1000) + the gap (500) = 1500, ends at 1500 + 1500 = 3000.
      { index: 1, startOffsetMs: 1500, durationMs: 1500, expectedEndMs: 3000 },
    ])
    expect(schedule.gapMs).toBe(500)
    expect(schedule.totalDurationMs).toBe(3000)
  })

  it('returns an empty schedule for no durations', () => {
    const schedule = computeSchedule([], 500)
    expect(schedule.utterances).toEqual([])
    expect(schedule.totalDurationMs).toBe(0)
  })
})
