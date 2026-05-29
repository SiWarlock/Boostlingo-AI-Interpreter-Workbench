import { describe, expect, it } from 'vitest'
import {
  clampBlobDurationMs,
  DEFAULT_BLOB_DURATION_MS,
  MAX_BLOB_DURATION_MS,
  micErrorToUiError,
  probeRecorderMimeType,
} from './captureSupport'

describe('probeRecorderMimeType', () => {
  it('returns the first supported type in the ARCH-030 probe order', () => {
    // Safari < 18.4: only mp4 supported -> mp4 (not webm).
    expect(probeRecorderMimeType((t) => t === 'audio/mp4')).toBe('audio/mp4')
    // everything supported -> the highest-priority candidate is returned first.
    expect(probeRecorderMimeType(() => true)).toBe('audio/webm;codecs=opus')
  })

  it('returns null when no candidate is supported', () => {
    expect(probeRecorderMimeType(() => false)).toBeNull()
  })
})

describe('micErrorToUiError', () => {
  it('maps mic error names to actionable UiErrors without leaking the raw message', () => {
    const denied = micErrorToUiError({ name: 'NotAllowedError', message: 'RAW-INTERNAL-DETAIL' })
    expect(denied.code).toBe('mic.permission_denied')
    expect(denied.safeMessage).not.toContain('RAW-INTERNAL-DETAIL')
    expect(denied.safeMessage.length).toBeGreaterThan(0)

    expect(micErrorToUiError({ name: 'SecurityError' }).code).toBe('mic.permission_denied')
    expect(micErrorToUiError({ name: 'NotFoundError' }).code).toBe('mic.not_found')
    expect(micErrorToUiError({ name: 'SomethingWeird' }).code).toBe('mic.unavailable')
    expect(micErrorToUiError(undefined).code).toBe('mic.unavailable')
  })
})

describe('clampBlobDurationMs', () => {
  it('caps an over-long duration and defaults a non-positive/non-finite one', () => {
    expect(clampBlobDurationMs(4000)).toBe(4000) // in-range passes through
    expect(clampBlobDurationMs(Number.MAX_SAFE_INTEGER)).toBe(MAX_BLOB_DURATION_MS) // capped (no unbounded record)
    expect(clampBlobDurationMs(0)).toBe(DEFAULT_BLOB_DURATION_MS)
    expect(clampBlobDurationMs(-5)).toBe(DEFAULT_BLOB_DURATION_MS)
    expect(clampBlobDurationMs(Number.NaN)).toBe(DEFAULT_BLOB_DURATION_MS)
  })
})
