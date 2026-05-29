import { describe, expect, it, vi } from 'vitest'
import {
  clampAudioContentType,
  createPlaybackGuard,
  createPlaybackStartedStamper,
  decodeBase64Audio,
} from './playbackController'
import type { LatencyEvent } from '../types/domain'

describe('decodeBase64Audio', () => {
  it('decodes a base64 audio chunk to the exact bytes; empty -> empty', () => {
    expect(Array.from(decodeBase64Audio('AAEC'))).toEqual([0, 1, 2]) // AAEC -> 0x00 0x01 0x02
    const decoded = decodeBase64Audio('')
    expect(decoded).toBeInstanceOf(Uint8Array)
    expect(decoded.length).toBe(0)
  })
})

describe('createPlaybackGuard (no overlap)', () => {
  it('stops the prior playback when a new one begins; clear() resets', () => {
    const guard = createPlaybackGuard()
    const stopFirst = vi.fn()
    const stopSecond = vi.fn()

    guard.begin(stopFirst)
    expect(guard.isActive()).toBe(true)

    guard.begin(stopSecond) // starting a second playback stops the first (single active)
    expect(stopFirst).toHaveBeenCalledTimes(1)
    expect(stopSecond).not.toHaveBeenCalled() // the new one isn't stopped

    guard.clear()
    expect(guard.isActive()).toBe(false)
  })
})

describe('clampAudioContentType', () => {
  it('passes known audio types (params stripped) and defaults anything else', () => {
    expect(clampAudioContentType('audio/mpeg')).toBe('audio/mpeg')
    expect(clampAudioContentType('audio/webm; codecs=opus')).toBe('audio/webm') // params stripped
    expect(clampAudioContentType('AUDIO/MP4')).toBe('audio/mp4') // case-insensitive
    // anything off the allowlist (or empty/garbage) -> the default, never accepted verbatim
    expect(clampAudioContentType('text/html')).toBe('audio/mpeg')
    expect(clampAudioContentType('')).toBe('audio/mpeg')
  })
})

describe('createPlaybackStartedStamper (playback.started, once per turn)', () => {
  it('builds the playback.started LatencyEvent and stamps it once, re-armed by reset', () => {
    const append = vi.fn<(event: LatencyEvent) => void>()
    const stamper = createPlaybackStartedStamper(append)

    stamper.stamp('2026-05-29T12:00:00.000Z')
    stamper.stamp('2026-05-29T12:00:01.000Z') // a second `playing` event for the same turn

    expect(append).toHaveBeenCalledTimes(1) // once per turn, not per chunk/event
    const event = append.mock.calls[0][0]
    expect(event.name).toBe('playback.started')
    expect(event.stage).toBe('playback')
    expect(event.clockSource).toBe('browser')
    expect(event.timestamp).toBe('2026-05-29T12:00:00.000Z')
    expect(event.relativeMs).toBe(0) // placeholder; the absolute timestamp is the datum (lesson §7)
    expect(event.metadata).toEqual({})

    stamper.reset() // a new turn re-arms the stamp
    stamper.stamp('2026-05-29T12:00:05.000Z')
    expect(append).toHaveBeenCalledTimes(2)
  })
})
