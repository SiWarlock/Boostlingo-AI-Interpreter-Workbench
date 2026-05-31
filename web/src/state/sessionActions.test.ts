import { describe, expect, it, vi } from 'vitest'
import { endSession, startSession } from './sessionActions'
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
