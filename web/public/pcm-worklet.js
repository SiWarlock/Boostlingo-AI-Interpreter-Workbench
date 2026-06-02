// Cascade capture worklet (ARCH-030) — runs in the AudioWorklet GLOBAL SCOPE (a separate JS realm).
//
// Shipped as a STATIC asset in web/public/ so it is served verbatim at /pcm-worklet.js in BOTH the Vite
// dev server AND the production build (Vite copies public/ to the build root → the .NET app serves it from
// wwwroot, same-origin). This is deliberate: `audioWorklet.addModule()` needs a real, fetchable URL, and a
// module with an `import` (the prior `new URL('./pcmWorklet.ts', import.meta.url)`) gets BUNDLED into the
// main chunk by Vite instead of emitted as a standalone asset → that URL 404s in the deployed build →
// addModule rejects → a spurious "mic.unavailable". Keeping the worklet import-free + static avoids that.
//
// The linear16 conversion below MIRRORS src/audio/pcm.ts `floatTo16BitPCM` (kept unit-tested there); if you
// change one, change both. AudioWorkletProcessor / registerProcessor / sampleRate are worklet-realm globals.

const FRAME_MS = 20 // ~20ms frames (ARCH-030 20–50ms band), at the actual context sample rate (no resample)

// Float32 [-1, 1] -> linear16 (Int16) PCM. Clamps so an out-of-range sample can't wrap + corrupt the audio.
function floatTo16BitPCM(input) {
  const output = new Int16Array(input.length)
  for (let i = 0; i < input.length; i++) {
    const clamped = Math.max(-1, Math.min(1, input[i]))
    // Asymmetric scale: -1 -> -32768 (0x8000), +1 -> 32767 (0x7fff).
    output[i] = clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff
  }
  return output
}

class PcmFrameProcessor extends AudioWorkletProcessor {
  constructor() {
    super()
    this.buffer = []
    this.frameSamples = Math.max(1, Math.round((sampleRate * FRAME_MS) / 1000))
  }

  process(inputs) {
    const channel = inputs[0] && inputs[0][0]
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
