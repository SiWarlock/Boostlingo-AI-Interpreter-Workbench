// 1×-real-time schedule computation for the soak harness (G.4 "Design constraints" — the synthetic graph
// plays at wall-clock speed so the 5-min soak genuinely takes 5 min). Given the runtime-decoded utterance
// durations (ms) + a configured inter-utterance gap, produces the cumulative start offsets + expected
// playback-end times — the deterministic timeline the overlap detector (soakDrift) reads against. Pure.

export type ScheduledUtterance = {
  index: number
  startOffsetMs: number
  durationMs: number
  expectedEndMs: number
}

export type SoakSchedule = {
  gapMs: number
  totalDurationMs: number
  utterances: ScheduledUtterance[]
}

export function computeSchedule(durationsMs: number[], gapMs: number): SoakSchedule {
  const utterances: ScheduledUtterance[] = []
  let cursor = 0
  for (let i = 0; i < durationsMs.length; i++) {
    const durationMs = durationsMs[i]
    const startOffsetMs = cursor
    const expectedEndMs = startOffsetMs + durationMs
    utterances.push({ index: i, startOffsetMs, durationMs, expectedEndMs })
    // The NEXT utterance starts one gap after this one's playback ends (no gap after the last).
    cursor = expectedEndMs + gapMs
  }
  const totalDurationMs =
    utterances.length > 0 ? utterances[utterances.length - 1].expectedEndMs : 0
  return { gapMs, totalDurationMs, utterances }
}
