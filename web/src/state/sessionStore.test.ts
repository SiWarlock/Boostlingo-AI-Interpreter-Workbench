import { describe, expect, it, vi } from 'vitest'
import { createSessionStore } from './sessionStore'
import type { ConfigResponse, InterpretationSession, UiError } from '../types/domain'

// Minimal ConfigResponse fixture (shape mirrors GET /api/config — ARCH-009).
const config: ConfigResponse = {
  realtime: { configured: true, models: ['gpt-realtime', 'gpt-realtime-mini'] },
  cascade: {
    stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
    translation: { configured: true, provider: 'openai', models: ['gpt-5.4-nano', 'gpt-5.4-mini'] },
    tts: { configured: true, provider: 'openai', model: 'gpt-4o-mini-tts' },
  },
  languages: ['en', 'es'],
  pricingConfigVersion: '2026-05-28-payg-estimates',
}

// Wire InterpretationSession fixture (POST /api/sessions response — top-level `sessionId`,
// mode/direction/models nested under config.*). turns/modeTransitions/summary deferred (opaque).
const wireSession: InterpretationSession = {
  sessionId: 'session_20260529T120000Z_abc123',
  label: 'demo',
  startedAt: '2026-05-29T12:00:00+00:00',
  config: {
    currentMode: 'realtime',
    direction: { source: 'es', target: 'en' },
    providerProfile: {
      realtimeProvider: 'openai',
      realtimeModel: 'gpt-realtime-mini',
      sttProvider: 'deepgram',
      sttModel: 'nova-3',
      sttLanguage: 'multi',
      translationProvider: 'openai',
      translationModel: 'gpt-5.4-mini',
      ttsProvider: 'openai',
      ttsModel: 'gpt-4o-mini-tts',
      ttsVoice: 'alloy',
    },
  },
  turns: [],
  modeTransitions: [],
  pricingConfigVersion: '2026-05-28-payg-estimates',
}

describe('sessionStore', () => {
  it('initial state matches the ARCH-007 defaults', () => {
    const store = createSessionStore()
    expect(store.getState()).toEqual({
      sessionId: null,
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
      realtimeModel: 'gpt-realtime',
      translationModel: 'gpt-5.4-nano',
      sessionStatus: 'idle',
      turnStatus: 'ready',
      turns: [],
      errors: [],
    })
  })

  it('loadConfig sets providerHealth from the config response (Flow A bootstrap)', () => {
    const store = createSessionStore()
    store.loadConfig(config)
    expect(store.getState().providerHealth).toEqual(config)
  })

  it('configureSession sets the selected fields and transitions idle -> configured', () => {
    const store = createSessionStore()
    store.configureSession({
      label: 'my run',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
      realtimeModel: 'gpt-realtime-mini',
      translationModel: 'gpt-5.4-mini',
    })
    const s = store.getState()
    expect(s.label).toBe('my run')
    expect(s.mode).toBe('cascade')
    expect(s.direction).toEqual({ source: 'en', target: 'es' })
    expect(s.realtimeModel).toBe('gpt-realtime-mini')
    expect(s.translationModel).toBe('gpt-5.4-mini')
    expect(s.sessionStatus).toBe('configured')
  })

  it('sessionStarted maps the wire session DTO into view state and goes active', () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession)
    const s = store.getState()
    expect(s.sessionId).toBe('session_20260529T120000Z_abc123')
    expect(s.mode).toBe('realtime') // from config.currentMode
    expect(s.direction).toEqual({ source: 'es', target: 'en' }) // from config.direction
    expect(s.realtimeModel).toBe('gpt-realtime-mini') // from config.providerProfile.realtimeModel
    expect(s.translationModel).toBe('gpt-5.4-mini') // from config.providerProfile.translationModel
    expect(s.sessionStatus).toBe('active')
  })

  it('setSessionStatus and setTurnStatus update their respective fields', () => {
    const store = createSessionStore()
    store.setSessionStatus('starting')
    store.setTurnStatus('recording')
    expect(store.getState().sessionStatus).toBe('starting')
    expect(store.getState().turnStatus).toBe('recording')
  })

  it('addError appends, clearErrors empties, sessionEnded ends, reset returns to initial', () => {
    const store = createSessionStore()
    const e1: UiError = { code: 'stt.timeout', safeMessage: 'STT timed out.', retryable: true }
    const e2: UiError = { code: 'tts.unknown', safeMessage: 'TTS failed.', retryable: false }
    store.addError(e1)
    store.addError(e2)
    expect(store.getState().errors).toEqual([e1, e2])

    store.clearErrors()
    expect(store.getState().errors).toEqual([])

    store.sessionEnded()
    expect(store.getState().sessionStatus).toBe('ended')

    store.reset()
    expect(store.getState().sessionStatus).toBe('idle')
    expect(store.getState().sessionId).toBeNull()
    expect(store.getState().errors).toEqual([])
  })

  it('subscribe notifies, unsubscribe stops, and state ref changes on mutation (stable when unread)', () => {
    const store = createSessionStore()
    const before = store.getState()
    // Stable reference across reads with no intervening action (useSyncExternalStore: no render loop).
    expect(store.getState()).toBe(before)

    const listener = vi.fn()
    const unsubscribe = store.subscribe(listener)
    store.setTurnStatus('recording')
    expect(listener).toHaveBeenCalledTimes(1)

    const after = store.getState()
    expect(after).not.toBe(before) // new object reference on mutation
    expect(after.turnStatus).toBe('recording')

    unsubscribe()
    store.setTurnStatus('processing')
    expect(listener).toHaveBeenCalledTimes(1) // no further notifications after unsubscribe
  })
})
