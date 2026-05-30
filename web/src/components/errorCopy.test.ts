import { describe, expect, it } from 'vitest'
import { errorCopy } from './errorCopy'
import type { UiError } from '../types/domain'

// errorCopy is a PURE code -> actionable-copy map (ARCH-007 frontend error philosophy / ARCH-018).
// It reads ONLY the sanitized UiError.code and returns fixed copy — it NEVER echoes safeMessage (even
// though that is already sanitized), so no raw provider text/stack can ever reach the UI through it.
function err(code: string, safeMessage = 'ignored-sanitized-message'): UiError {
  return { code, safeMessage, retryable: true }
}

describe('errorCopy', () => {
  it('maps known codes to fixed, actionable copy', () => {
    const mic = errorCopy(err('mic.permission_denied'))
    expect(mic).toMatch(/microphone permission denied/i)
    expect(mic).toMatch(/retry/i) // actionable

    expect(errorCopy(err('stt.timeout'))).toMatch(/stt|speech-to-text/i)
    expect(errorCopy(err('translation.unknown'))).toMatch(/translation/i)
    expect(errorCopy(err('tts.unknown'))).toMatch(/tts|text-to-speech/i)
    expect(errorCopy(err('realtime.session.disconnected'))).toMatch(/realtime/i)
    expect(errorCopy(err('persistence.failed'))).toMatch(/may not have been saved/i)

    // specific-code branches (real app-emitted codes — pin them so the switch can't silently fall through)
    expect(errorCopy(err('mic.not_found'))).toMatch(/no microphone/i)
    expect(errorCopy(err('mic.unavailable'))).toMatch(/unavailable/i)
    expect(errorCopy(err('config.load_failed'))).toMatch(/configuration/i)
    expect(errorCopy(err('summary.load_failed'))).toMatch(/summary/i)
    expect(errorCopy(err('turn.create_failed'))).toMatch(/start the turn/i)
    expect(errorCopy(err('rate_limited'))).toMatch(/rate-limiting/i)
  })

  it('falls back to a safe generic for an unknown code (never an error, never raw)', () => {
    expect(errorCopy(err('mystery.boom'))).toBe('Something went wrong. Please retry.')
  })

  it('NEVER echoes the raw safeMessage — renders only fixed copy (ARCH-007/018)', () => {
    const leak = 'RAW-PROVIDER-LEAK sk-secret stack@frame'
    // a mapped code
    expect(errorCopy(err('stt.timeout', leak))).not.toContain('RAW-PROVIDER-LEAK')
    // an unmapped code -> generic, still no leak
    expect(errorCopy(err('totally.unknown', leak))).not.toContain('RAW-PROVIDER-LEAK')
  })
})
