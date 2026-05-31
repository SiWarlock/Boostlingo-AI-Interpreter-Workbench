import { describe, expect, it, vi } from 'vitest'
import { endSession, startSession, switchMode } from './sessionActions'
import { createSessionStore } from './sessionStore'
import { ApiError } from '../api/http'
import type { EndSessionResponse, InterpretationSession, UiError } from '../types/domain'

function wireSession(overrides: Partial<InterpretationSession> = {}): InterpretationSession {
  return {
    sessionId: 'session_abc',
    startedAt: '2026-05-29T12:00:00+00:00',
    config: {
      currentMode: 'cascade',
      direction: { source: 'en', target: 'es' },
      providerProfile: {
        realtimeProvider: 'openai',
        realtimeModel: 'gpt-realtime',
        sttProvider: 'deepgram',
        sttModel: 'nova-3',
        sttLanguage: 'multi',
        translationProvider: 'openai',
        translationModel: 'gpt-5-nano',
        ttsProvider: 'openai',
        ttsModel: 'gpt-4o-mini-tts',
        ttsVoice: 'alloy',
      },
    },
    turns: [],
    modeTransitions: [],
    pricingConfigVersion: '2026-05-28-payg-estimates',
    ...overrides,
  }
}

describe('startSession', () => {
  it('clears prior errors first, sets starting, creates from store-derived request, then starts', async () => {
    const store = createSessionStore()
    // live form state lives in the store (D.2: updateSessionConfig on each edit)
    store.updateSessionConfig({
      label: 'demo',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
      realtimeModel: 'gpt-realtime',
      translationModel: 'gpt-5-nano',
    })
    store.addError({ code: 'config.load_failed', safeMessage: 'old error', retryable: true })
    expect(store.getState().errors).toHaveLength(1)

    const created = wireSession()
    const api = { createSession: vi.fn().mockResolvedValue(created), endSession: vi.fn() }
    const clearSpy = vi.spyOn(store, 'clearErrors')

    await startSession({ store, api })

    // request derived from store state (NOT a separate formInput)
    expect(api.createSession).toHaveBeenCalledWith({
      label: 'demo',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
      realtimeModel: 'gpt-realtime',
      translationModel: 'gpt-5-nano',
    })
    // clearErrors runs BEFORE createSession so a prior error can't survive a successful start
    expect(clearSpy.mock.invocationCallOrder[0]).toBeLessThan(
      api.createSession.mock.invocationCallOrder[0],
    )

    const s = store.getState()
    expect(s.errors).toEqual([]) // prior error gone
    expect(s.sessionId).toBe('session_abc')
    expect(s.sessionStatus).toBe('active')
  })

  it('on ApiError clears the prior error first, adds the uiError, and reverts to configured', async () => {
    const store = createSessionStore()
    store.updateSessionConfig({
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
      realtimeModel: 'gpt-realtime',
      translationModel: 'gpt-5-nano',
    })
    const prior: UiError = { code: 'prior', safeMessage: 'prior', retryable: false }
    store.addError(prior)
    const uiError: UiError = {
      code: 'rate_limited',
      safeMessage: 'Too many requests.',
      retryable: true,
    }
    const api = {
      createSession: vi.fn().mockRejectedValue(new ApiError(uiError)),
      endSession: vi.fn(),
    }
    const clearSpy = vi.spyOn(store, 'clearErrors')

    await startSession({ store, api })

    // clearErrors runs before createSession on the error path too (not only the happy path)
    expect(clearSpy.mock.invocationCallOrder[0]).toBeLessThan(
      api.createSession.mock.invocationCallOrder[0],
    )
    const s = store.getState()
    expect(s.errors).toContainEqual(uiError)
    expect(s.errors).not.toContainEqual(prior) // prior cleared before the attempt
    expect(s.sessionStatus).toBe('configured') // reverted, not stuck on 'starting'
    expect(s.sessionId).toBeNull()
  })

  it('on a non-ApiError throw synthesizes a fixed-message error without leaking the raw error', async () => {
    const store = createSessionStore()
    store.updateSessionConfig({
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
      realtimeModel: 'gpt-realtime',
      translationModel: 'gpt-5-nano',
    })
    const api = {
      createSession: vi.fn().mockRejectedValue(new Error('boom-internal-detail')),
      endSession: vi.fn(),
    }

    await startSession({ store, api })

    const s = store.getState()
    expect(s.errors).toHaveLength(1)
    expect(s.errors[0].code).toBe('session.start_failed')
    expect(s.errors[0].safeMessage).not.toContain('boom-internal-detail')
    expect(s.sessionStatus).toBe('configured')
  })
})

describe('endSession', () => {
  it('ends the session and surfaces a persistence warning when present', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // seeds sessionId + active
    const warning: UiError = {
      code: 'persistence.failed',
      safeMessage: 'Session may not have been saved.',
      retryable: false,
    }
    const res: EndSessionResponse = { session: wireSession(), persistenceWarning: warning }
    const api = { createSession: vi.fn(), endSession: vi.fn().mockResolvedValue(res) }

    await endSession({ store, api })

    expect(api.endSession).toHaveBeenCalledWith('session_abc')
    const s = store.getState()
    expect(s.sessionStatus).toBe('ended')
    expect(s.errors).toContainEqual(warning)
  })

  it('ends cleanly with no error when there is no persistence warning', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const res: EndSessionResponse = { session: wireSession(), persistedPath: 'session_abc.json' }
    const api = { createSession: vi.fn(), endSession: vi.fn().mockResolvedValue(res) }

    await endSession({ store, api })

    expect(store.getState().sessionStatus).toBe('ended')
    expect(store.getState().errors).toEqual([])
  })

  it('on ApiError surfaces the error and keeps the session active (end did not happen server-side)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // active, sessionId set
    const uiError: UiError = {
      code: 'network.error',
      safeMessage: 'Could not reach the server.',
      retryable: true,
    }
    const api = {
      createSession: vi.fn(),
      endSession: vi.fn().mockRejectedValue(new ApiError(uiError)),
    }

    await endSession({ store, api })

    const s = store.getState()
    expect(s.errors).toContainEqual(uiError)
    expect(s.sessionStatus).toBe('active') // NOT 'ended' — the server-side end didn't happen
  })

  it('is a no-op (no api call) when there is no active session', async () => {
    const store = createSessionStore() // sessionId null
    const api = { createSession: vi.fn(), endSession: vi.fn() }

    await endSession({ store, api })

    expect(api.endSession).not.toHaveBeenCalled()
    expect(store.getState().sessionStatus).toBe('idle')
  })

  it('on a non-ApiError throw synthesizes a fixed-message error and keeps the session active', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const api = {
      createSession: vi.fn(),
      endSession: vi.fn().mockRejectedValue(new Error('boom-internal-detail')),
    }

    await endSession({ store, api })

    const s = store.getState()
    expect(s.errors).toHaveLength(1)
    expect(s.errors[0].code).toBe('session.end_failed')
    expect(s.errors[0].safeMessage).not.toContain('boom-internal-detail')
    expect(s.sessionStatus).toBe('active') // end didn't happen
  })
})

describe('switchMode (Finding 2c — propagate a mid-session mode switch to the backend)', () => {
  const cm = () => ({ onModeSwitch: vi.fn() })

  // A wireSession with config.currentMode overridden (the authoritative response after a switch).
  function sessionWithMode(
    mode: InterpretationSession['config']['currentMode'],
  ): InterpretationSession {
    const s = wireSession()
    s.config = { ...s.config, currentMode: mode }
    return s
  }

  it('active session: POSTs setMode, resyncs the mode from the response, tears down on switch (success)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // currentMode cascade -> store.mode 'cascade'
    const api = { setMode: vi.fn().mockResolvedValue(sessionWithMode('realtime')) }
    const connectionManager = cm()

    await switchMode({ store, api, connectionManager }, 'realtime')

    expect(api.setMode).toHaveBeenCalledWith('session_abc', 'realtime')
    expect(connectionManager.onModeSwitch).toHaveBeenCalledWith('cascade', 'realtime')
    expect(store.getState().mode).toBe('realtime') // resynced from response.config.currentMode (Q4)
  })

  it('on ApiError: normalizes to session.mode_switch_failed (Q4), KEEPS the prior mode, no teardown', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // cascade
    const uiError: UiError = {
      code: 'session.invalid_mode',
      safeMessage: 'That mode is not available.',
      retryable: false,
    }
    const api = { setMode: vi.fn().mockRejectedValue(new ApiError(uiError)) }
    const connectionManager = cm()

    await switchMode({ store, api, connectionManager }, 'realtime')

    const s = store.getState()
    // Q4 (054 — intentional behavior change): the catch normalizes ANY failure (ApiError or not) to a
    // single sanitized frontend code, so errorCopy maps ONE actionable message and no raw backend/http
    // code (e.g. http.404 pre-050-backend) reaches the banner via the generic fallback.
    expect(s.errors).toHaveLength(1)
    expect(s.errors[0].code).toBe('session.mode_switch_failed')
    expect(s.errors[0].safeMessage).not.toContain('That mode is not available.') // backend msg not echoed
    expect(s.mode).toBe('cascade') // prior mode kept — no backend/frontend divergence (Q1)
    expect(connectionManager.onModeSwitch).not.toHaveBeenCalled() // no teardown on a failed switch
  })

  it('on a non-ApiError throw: synthesizes a fixed-message error and keeps the prior mode', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const api = { setMode: vi.fn().mockRejectedValue(new Error('boom-internal-detail')) }

    await switchMode({ store, api, connectionManager: cm() }, 'realtime')

    const s = store.getState()
    expect(s.errors).toHaveLength(1)
    expect(s.errors[0].code).toBe('session.mode_switch_failed')
    expect(s.errors[0].safeMessage).not.toContain('boom-internal-detail')
    expect(s.mode).toBe('cascade')
  })

  it('clears prior errors before attempting a switch — a failed switch leaves ONLY the normalized error', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // active, cascade
    store.addError({ code: 'prior.error', safeMessage: 'old', retryable: true }) // a lingering banner
    const api = { setMode: vi.fn().mockRejectedValue(new Error('boom')) }
    const connectionManager = cm()

    await switchMode({ store, api, connectionManager }, 'realtime')

    // switchMode self-clears at the start of a real switch attempt (like startSession, §7) so it's robust
    // to any callsite — not only ModeToggle. The prior error is gone; only the new normalized error remains.
    const s = store.getState()
    expect(s.errors).toHaveLength(1) // RED today: switchMode doesn't clear → prior + new = length 2
    expect(s.errors[0].code).toBe('session.mode_switch_failed')
    expect(s.mode).toBe('cascade')
  })

  it('no-op when the target equals the current mode (no POST, no teardown)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // cascade
    const api = { setMode: vi.fn() }
    const connectionManager = cm()

    await switchMode({ store, api, connectionManager }, 'cascade')

    expect(api.setMode).not.toHaveBeenCalled()
    expect(connectionManager.onModeSwitch).not.toHaveBeenCalled()
    expect(store.getState().mode).toBe('cascade')
  })

  it('pre-session (no sessionId): store-only switch, NO POST (Create sends the initial mode)', async () => {
    const store = createSessionStore() // sessionId null
    store.updateSessionConfig({ mode: 'cascade' })
    const api = { setMode: vi.fn() }
    const connectionManager = cm()

    await switchMode({ store, api, connectionManager }, 'realtime')

    expect(api.setMode).not.toHaveBeenCalled()
    expect(connectionManager.onModeSwitch).toHaveBeenCalledWith('cascade', 'realtime')
    expect(store.getState().mode).toBe('realtime')
  })

  it('ended session: store-only switch, NO POST — do not POST to a dead session (054 Fix A)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // active, sessionId 'session_abc', cascade
    store.sessionEnded() // sessionStatus 'ended' — but sessionId is NOT cleared (the root-cause gap)
    const api = { setMode: vi.fn() }
    const connectionManager = cm()

    await switchMode({ store, api, connectionManager }, 'realtime')

    expect(api.setMode).not.toHaveBeenCalled() // no POST to the ended session (RED today: sessionId non-null → POSTs)
    expect(connectionManager.onModeSwitch).toHaveBeenCalledWith('cascade', 'realtime') // same shape as pre-session
    expect(store.getState().mode).toBe('realtime') // store-only write — configures the NEXT session's mode
    expect(store.getState().errors).toEqual([]) // no error — the toggle self-recovers (no dead-endpoint 404)
  })

  it('ending session: store-only switch, NO POST (gate is future-proof for the in-flight-end status)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // active, cascade
    store.setSessionStatus('ending')
    const api = { setMode: vi.fn() }
    const connectionManager = cm()

    await switchMode({ store, api, connectionManager }, 'realtime')

    expect(api.setMode).not.toHaveBeenCalled()
    expect(connectionManager.onModeSwitch).toHaveBeenCalledWith('cascade', 'realtime') // store-only branch shape
    expect(store.getState().mode).toBe('realtime')
    expect(store.getState().errors).toEqual([])
  })

  it('readyForTurn session: POSTs setMode — a LIVE session, the inverse gate must NOT skip it (regression guard vs `=== active`)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession()) // active, cascade
    store.setSessionStatus('readyForTurn') // a live between-turns status — still a backend session
    const api = { setMode: vi.fn().mockResolvedValue(sessionWithMode('realtime')) }
    const connectionManager = cm()

    await switchMode({ store, api, connectionManager }, 'realtime')

    // Guards the Q2 inverse gate: a naive refactor to `sessionStatus === 'active'` would skip this POST
    // and silently re-introduce the 2c divergence. (Green against both the old sessionId-only guard and
    // the new inverse gate — a deliberate regression lock, not a RED-first behavior test.)
    expect(api.setMode).toHaveBeenCalledWith('session_abc', 'realtime')
    expect(connectionManager.onModeSwitch).toHaveBeenCalledWith('cascade', 'realtime')
    expect(store.getState().mode).toBe('realtime') // resynced from the response
  })
})
