import type { SoakSchedule } from './soakSchedule'

// The synthetic-stream generator for the G.4 soak harness (decision 1A + Q4). On a harness-owned
// AudioContext, decoded TTS buffers → AudioBufferSourceNodes → one MediaStreamAudioDestinationNode →
// `.stream` — the single real MediaStream injected at each mode's `getUserMedia` (cascade worklet→PCM→WS;
// realtime WebRTC track). Each source starts at its 087-computed 1×-real-time offset, so the conversation
// plays at wall-clock speed. The browser-audio nodes are manual-smoke; the schedule it consumes is TDD'd.

// The minimal AudioContext surface the generator needs — keeps the export `any`-free while letting a fake
// stand in for the browser-only AudioContext in tests (a real AudioContext is assignable).
export type SyntheticStreamContext = Pick<
  AudioContext,
  'currentTime' | 'createBufferSource' | 'createMediaStreamDestination'
>

export type SyntheticAudioStreamDeps = {
  context: SyntheticStreamContext
  // Decoded utterance buffers, ordered to match `schedule.utterances` (089 builds both consistently).
  buffers: AudioBuffer[]
  schedule: SoakSchedule
}

export type SyntheticAudioStream = {
  stream: MediaStream
  start: () => void
  stop: () => void
}

export function createSyntheticAudioStream(deps: SyntheticAudioStreamDeps): SyntheticAudioStream {
  const { context, buffers, schedule } = deps
  const destination = context.createMediaStreamDestination()
  let sources: AudioBufferSourceNode[] = []

  function start(): void {
    // Anchor every utterance to ONE base time so the offsets stay relative to a single run origin.
    const base = context.currentTime
    sources = buffers.map((buffer, i) => {
      const source = context.createBufferSource()
      source.buffer = buffer
      source.connect(destination)
      const offsetMs = schedule.utterances[i]?.startOffsetMs ?? 0
      source.start(base + offsetMs / 1000)
      return source
    })
  }

  function stop(): void {
    sources.forEach((source) => source.stop())
    sources = []
  }

  return { stream: destination.stream, start, stop }
}
