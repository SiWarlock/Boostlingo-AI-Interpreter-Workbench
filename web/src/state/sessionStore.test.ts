import { describe, expect, it, vi } from 'vitest'
import { createSessionStore } from './sessionStore'
import type {
  ConfigResponse,
  CostEstimate,
  InterpretationSession,
  LatencyEvent,
  TranscriptSegment,
  UiError,
} from '../types/domain'

// Minimal ConfigResponse fixture (shape mirrors GET /api/config — ARCH-009).
const config: ConfigResponse = {
  realtime: { configured: true, models: ['gpt-realtime', 'gpt-realtime-mini'] },
  cascade: {
    stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
    translation: { configured: true, provider: 'openai', models: ['gpt-5-nano', 'gpt-5-mini'] },
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
      translationModel: 'gpt-5-mini',
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
      translationModel: 'gpt-5-nano',
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

  it('sessionStarted maps the wire session DTO into view state and goes active', () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession)
    const s = store.getState()
    expect(s.sessionId).toBe('session_20260529T120000Z_abc123')
    expect(s.mode).toBe('realtime') // from config.currentMode
    expect(s.direction).toEqual({ source: 'es', target: 'en' }) // from config.direction
    expect(s.realtimeModel).toBe('gpt-realtime-mini') // from config.providerProfile.realtimeModel
    expect(s.translationModel).toBe('gpt-5-mini') // from config.providerProfile.translationModel
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

  it('setSummary stores the backend session summary (the MetricsPanel session-averages source, D.6)', () => {
    const store = createSessionStore()
    const summary = {
      turnCount: 2,
      cascade: {
        turnCount: 2,
        avgSpeechEndToFirstAudioMs: null,
        avgSpeechEndToPlaybackMs: null,
        estimatedCostPerMinuteUsd: 0.5,
        errorCount: 0,
        avgSttFinalMs: 120,
        avgTranslationFinalMs: 240,
        avgTtsFirstAudioMs: 360,
      },
      computedAt: '2026-05-29T12:00:00+00:00',
      pricingConfigVersion: '2026-05-28-payg-estimates',
    }
    store.setSummary(summary)
    expect(store.getState().summary).toEqual(summary)
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

  it('updateSessionConfig with a full patch from idle sets all fields and configures (folds D.1 configureSession)', () => {
    const store = createSessionStore()
    store.updateSessionConfig({
      label: 'my run',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
      realtimeModel: 'gpt-realtime-mini',
      translationModel: 'gpt-5-mini',
    })
    const s = store.getState()
    expect(s.label).toBe('my run')
    expect(s.mode).toBe('cascade')
    expect(s.direction).toEqual({ source: 'en', target: 'es' })
    expect(s.realtimeModel).toBe('gpt-realtime-mini')
    expect(s.translationModel).toBe('gpt-5-mini')
    expect(s.sessionStatus).toBe('configured')
  })

  it('updateSessionConfig merges a partial patch, transitions to configured, notifies (D.2)', () => {
    const store = createSessionStore()
    const listener = vi.fn()
    store.subscribe(listener)

    const before = store.getState()
    store.updateSessionConfig({ mode: 'realtime' })
    const after = store.getState()

    expect(after).not.toBe(before) // new ref
    expect(listener).toHaveBeenCalledTimes(1) // notifies
    expect(after.mode).toBe('realtime')
    expect(after.sessionStatus).toBe('configured') // idle -> configured

    // a second partial merges without clobbering the prior field
    store.updateSessionConfig({ label: 'run-1' })
    const final = store.getState()
    expect(final.mode).toBe('realtime') // preserved
    expect(final.label).toBe('run-1')
    expect(final.sessionStatus).toBe('configured')
  })

  it('updateSessionConfig does NOT drag an active session back to configured (mode switch between turns)', () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession) // -> active, mode 'realtime'
    store.updateSessionConfig({ mode: 'cascade' })
    const s = store.getState()
    expect(s.mode).toBe('cascade') // the mode write still applies
    expect(s.sessionStatus).toBe('active') // NOT reset to 'configured' (Flow-G between-turns switch)
  })
})

describe('sessionStore streaming actions (D.4a)', () => {
  function sourceSeg(text: string, isFinal: boolean): TranscriptSegment {
    return {
      segmentId: 'seg',
      role: 'source',
      text,
      isFinal,
      provider: 'deepgram',
      timestamp: '2026-05-29T12:00:00+00:00',
      clockSource: 'server',
    }
  }

  it('beginTurn sets currentTurn (empty transcripts, recording) and turnStatus recording', () => {
    const store = createSessionStore()
    const before = store.getState()
    store.beginTurn({
      turnId: 'turn_001',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })
    const s = store.getState()
    expect(s).not.toBe(before)
    expect(s.currentTurn?.turnId).toBe('turn_001')
    expect(s.currentTurn?.status).toBe('recording')
    expect(s.currentTurn?.sourceTranscript).toEqual([])
    expect(s.currentTurn?.targetTranscript).toEqual([])
    expect(s.turnStatus).toBe('recording')
  })

  it('appendTranscriptSegment replaces the trailing partial and finalizes on isFinal (ARCH-011)', () => {
    const store = createSessionStore()
    store.beginTurn({ turnId: 't', mode: 'cascade', direction: { source: 'en', target: 'es' } })

    store.appendTranscriptSegment(sourceSeg('ho', false))
    expect(store.getState().currentTurn?.sourceTranscript).toEqual([{ text: 'ho', isFinal: false }])

    store.appendTranscriptSegment(sourceSeg('hola', false)) // replaces the running partial
    expect(store.getState().currentTurn?.sourceTranscript).toEqual([
      { text: 'hola', isFinal: false },
    ])

    store.appendTranscriptSegment(sourceSeg('hola mundo', true)) // finalizes the trailing entry
    expect(store.getState().currentTurn?.sourceTranscript).toEqual([
      { text: 'hola mundo', isFinal: true },
    ])

    store.appendTranscriptSegment(sourceSeg('que', false)) // a partial after a final starts a NEW entry
    expect(store.getState().currentTurn?.sourceTranscript).toEqual([
      { text: 'hola mundo', isFinal: true },
      { text: 'que', isFinal: false },
    ])

    // a target-role segment routes to targetTranscript, not source.
    store.appendTranscriptSegment({ ...sourceSeg('hello', false), role: 'target' })
    expect(store.getState().currentTurn?.targetTranscript).toEqual([
      { text: 'hello', isFinal: false },
    ])
  })

  it('appendLatencyEvent records per-stage timing and setTurnCost sets the cost fields', () => {
    const store = createSessionStore()
    store.beginTurn({ turnId: 't', mode: 'cascade', direction: { source: 'en', target: 'es' } })

    const event: LatencyEvent = {
      name: 'stt.final',
      stage: 'stt',
      timestamp: '2026-05-29T12:00:00+00:00',
      relativeMs: 250,
      clockSource: 'server',
      metadata: {},
    }
    store.appendLatencyEvent(event)
    expect(store.getState().currentTurn?.latency.stages?.['stt.final']).toBe(250)
    // D.6: the RAW event (with its absolute timestamp) is also retained on the turn timeline so
    // deriveTurnMetrics can compute top-level deltas via absolute-timestamp Between (the stages map
    // keeps only relativeMs, which must never be used for cross-event math — lesson §7).
    expect(store.getState().currentTurn?.latencyEvents).toContainEqual(event)

    const estimate: CostEstimate = {
      provider: 'cascade',
      model: 'gpt-5-nano',
      pricingBasis: 'composite',
      estimatedUsd: 0.012,
      estimatedUsdPerMinute: 0.6,
      units: {},
      pricingConfigVersion: 'v1',
      assumptions: ['TTS cost uses a character-count proxy'],
    }
    store.setTurnCost(estimate)
    const turn = store.getState().currentTurn
    expect(turn?.estimatedCostUsd).toBe(0.012)
    expect(turn?.estimatedCostPerMinuteUsd).toBe(0.6)
    expect(turn?.translationModelUsed).toBe('gpt-5-nano')
    // D.6: the FULL estimate is retained too (the CostPanel renders model + the assumptions tooltip).
    expect(turn?.cost).toEqual(estimate)
  })

  it('failTurn records the error + marks failed; completeTurn finalizes currentTurn into turns[]', () => {
    const store = createSessionStore()
    store.beginTurn({
      turnId: 'turn_001',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })

    const uiError: UiError = { code: 'stt.timeout', safeMessage: 'STT timed out.', retryable: true }
    store.failTurn(uiError)
    expect(store.getState().errors).toContainEqual(uiError)
    expect(store.getState().currentTurn?.status).toBe('failed')
    expect(store.getState().turnStatus).toBe('failed')

    store.completeTurn('turn_001', 'completed')
    const s = store.getState()
    expect(s.currentTurn).toBeUndefined() // finalized + cleared
    expect(s.turns).toHaveLength(1)
    expect(s.turns[0].turnId).toBe('turn_001')
    expect(s.turnStatus).toBe('completed')
  })

  it('completeTurn for a non-current turnId leaves the active turn untouched (stale done ignored)', () => {
    const store = createSessionStore()
    store.beginTurn({
      turnId: 'turn_002',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })

    store.completeTurn('turn_001', 'completed') // a done for a DIFFERENT (stale) turn

    const s = store.getState()
    expect(s.currentTurn?.turnId).toBe('turn_002') // active turn not clobbered
    expect(s.turns).toHaveLength(0) // nothing finalized
    expect(s.turnStatus).toBe('recording') // not flipped to completed by a stale done
    // a stale done must not leak a turn.completed onto the still-live turn
    expect(s.currentTurn?.latencyEvents ?? []).not.toContainEqual(
      expect.objectContaining({ name: 'turn.completed' }),
    )
  })

  // D.6 (orchestrator ADD): the backend stamps turn.completed on finalize but does NOT stream it over
  // the cascade WS — the frontend's turn-complete signal is the `done` message → completeTurn. So
  // completeTurn stamps a browser-clock turn.completed onto the finalized turn timeline; this is the
  // canonical totalTurn endpoint (recording.started → turn.completed, both browser-clock = clean),
  // with tts.complete (server, cross-clock) as the deriveTurnMetrics fallback.
  it('completeTurn stamps a browser-clock turn.completed onto the finalized turn (totalTurn endpoint)', () => {
    const store = createSessionStore()
    store.beginTurn({
      turnId: 'turn_001',
      mode: 'cascade',
      direction: { source: 'en', target: 'es' },
    })

    store.completeTurn('turn_001', 'completed')

    const finalized = store.getState().turns[0]
    const completedEvent = finalized.latencyEvents?.find((e) => e.name === 'turn.completed')
    expect(completedEvent).toMatchObject({
      name: 'turn.completed',
      stage: 'overall',
      clockSource: 'browser',
    })
    expect(Number.isNaN(Date.parse(completedEvent!.timestamp))).toBe(false) // a real ISO timestamp
  })
})
