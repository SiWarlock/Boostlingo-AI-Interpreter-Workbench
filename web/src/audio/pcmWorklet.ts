import { floatTo16BitPCM } from './pcm'

// This module runs in the AudioWorklet GLOBAL SCOPE — a separate JS realm from the main thread, which
// the standard DOM lib does not type. Declare the minimal surface used here (avoids a @types dep for
// ~3 globals). Manual-smoke (ARCH-020): the corruption-prone part (the linear16 conversion) is
// unit-tested via the imported pcm.ts; the worklet wiring itself is browser-smoked (demo checklist).
declare const sampleRate: number
declare class AudioWorkletProcessor {
  readonly port: MessagePort
  constructor()
}
declare function registerProcessor(
  name: string,
  processorCtor: new () => AudioWorkletProcessor,
): void

// ~20ms frames (ARCH-030 20–50ms band) accumulated from the worklet's 128-sample process() blocks,
// at the ACTUAL context sample rate (no resample).
const FRAME_MS = 20

class PcmFrameProcessor extends AudioWorkletProcessor {
  private readonly buffer: number[] = []
  private readonly frameSamples = Math.max(1, Math.round((sampleRate * FRAME_MS) / 1000))

  process(inputs: Float32Array[][]): boolean {
    const channel = inputs[0]?.[0]
    if (channel) {
      for (let i = 0; i < channel.length; i++) {
        this.buffer.push(channel[i])
      }
      while (this.buffer.length >= this.frameSamples) {
        const frame = floatTo16BitPCM(Float32Array.from(this.buffer.splice(0, this.frameSamples)))
        this.port.postMessage(frame.buffer, [frame.buffer])
      }
    }
    return true // keep the processor alive across render quanta
  }
}

registerProcessor('pcm-frame-processor', PcmFrameProcessor)
