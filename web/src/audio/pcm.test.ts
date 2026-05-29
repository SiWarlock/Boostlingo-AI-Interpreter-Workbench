import { describe, expect, it } from 'vitest'
import { floatTo16BitPCM } from './pcm'

describe('floatTo16BitPCM', () => {
  it('scales and clamps Float32 samples to Int16 without wraparound', () => {
    const out = floatTo16BitPCM(Float32Array.from([0, 1.0, -1.0, 1.5, -1.5]))
    expect(out).toBeInstanceOf(Int16Array)
    expect(out[0]).toBe(0)
    expect(out[1]).toBe(32767) // 1.0 -> max int16
    expect(out[2]).toBe(-32768) // -1.0 -> min int16
    expect(out[3]).toBe(32767) // 1.5 clamps to max (no wraparound -> would corrupt audio)
    expect(out[4]).toBe(-32768) // -1.5 clamps to min
  })

  it('uses the asymmetric int16 scale for mid-range samples', () => {
    const out = floatTo16BitPCM(Float32Array.from([0.5, -0.5]))
    expect(out[0]).toBe(16383) // 0.5 * 0x7fff = 16383.5 -> truncates to 16383 (NOT 16384 from *0x8000)
    expect(out[1]).toBe(-16384) // -0.5 * 0x8000 = -16384
  })

  it('preserves length; empty input -> empty output', () => {
    expect(floatTo16BitPCM(Float32Array.from([0.1, 0.2, 0.3])).length).toBe(3)
    expect(floatTo16BitPCM(new Float32Array(0)).length).toBe(0)
  })
})
