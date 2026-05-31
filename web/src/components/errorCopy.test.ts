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
    const disconnected = errorCopy(err('realtime.session.disconnected'))
    expect(disconnected).toMatch(/lost/i) // the specific disconnect copy (E.5a) — not the generic realtime fallback
    expect(disconnected).toMatch(/cascade/i) // advise switch-to-Cascade (ARCH-010)
    expect(errorCopy(err('persistence.failed'))).toMatch(/may not have been saved/i)

    // specific-code branches (real app-emitted codes — pin them so the switch can't silently fall through)
    expect(errorCopy(err('mic.not_found'))).toMatch(/no microphone/i)
    expect(errorCopy(err('mic.unavailable'))).toMatch(/unavailable/i)
    expect(errorCopy(err('config.load_failed'))).toMatch(/configuration/i)
    expect(errorCopy(err('summary.load_failed'))).toMatch(/summary/i)
    expect(errorCopy(err('turn.create_failed'))).toMatch(/start the turn/i)
    expect(errorCopy(err('rate_limited'))).toMatch(/rate-limiting/i)

    // mode-switch failure (G.4/054 Fix C) — actionable + NOT the generic fallback. switchMode normalizes
    // every failure to this single code (Q4), so this is the only mode-switch copy the banner ever shows.
    const modeSwitch = errorCopy(err('session.mode_switch_failed'))
    expect(modeSwitch).not.toBe('Something went wrong. Please retry.') // not the generic fallback
    expect(modeSwitch).toMatch(/mode/i)
    expect(modeSwitch).toMatch(/retry/i) // actionable

    // capture-error path (G.4/060) — both the new empty-blob code AND the existing mic-fail code must be
    // actionable, never the generic fallback (both currently fall through — the discovered adjacent gap).
    const captureEmpty = errorCopy(err('capture.empty'))
    expect(captureEmpty).not.toBe('Something went wrong. Please retry.')
    expect(captureEmpty).toMatch(/audio|captured|mic/i)
    const captureFailed = errorCopy(err('capture.failed'))
    expect(captureFailed).not.toBe('Something went wrong. Please retry.')
    expect(captureFailed).toMatch(/record|audio|mic/i)

    // session-history read failure (H.3/067) — actionable + NOT the generic fallback (the GET /api/sessions
    // list fetch can 500 → §35 sessions.read_failed; don't show the bare generic).
    const historyRead = errorCopy(err('sessions.read_failed'))
    expect(historyRead).not.toBe('Something went wrong. Please retry.')
    expect(historyRead).toMatch(/session|history|past/i)
    expect(historyRead).toMatch(/retry|refresh/i) // actionable
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
