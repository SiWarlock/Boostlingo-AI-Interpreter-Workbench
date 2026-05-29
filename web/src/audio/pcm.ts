// Float32 [-1, 1] -> linear16 (Int16) PCM, the conversion the capture worklet performs before frames
// go over the cascade WS (ARCH-030; encoding 'linear16'). Clamps out-of-range samples so a value
// outside [-1, 1] can't wrap around and corrupt the audio Deepgram receives. Pure + unit-tested so it
// can be verified outside the AudioWorklet realm (the worklet imports it).
export function floatTo16BitPCM(input: Float32Array): Int16Array {
  const output = new Int16Array(input.length)
  for (let i = 0; i < input.length; i++) {
    const clamped = Math.max(-1, Math.min(1, input[i]))
    // Asymmetric scale: -1 -> -32768 (0x8000), +1 -> 32767 (0x7fff).
    output[i] = clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff
  }
  return output
}
