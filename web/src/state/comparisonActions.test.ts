import { describe, expect, it, vi } from 'vitest'
import { loadComparison } from './comparisonActions'
import { createSessionStore } from './sessionStore'
import { ApiError } from '../api/http'
import type { CostEstimate, InterpretationSession, SessionSummary, UiError } from '../types/domain'

function wireSession(overrides: Partial<InterpretationSession> = {}): InterpretationSession {
  return {
    sessionId: 'session_abc',
    startedAt: '2026-05-30T00:00:00+00:00',
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
    pricingConfigVersion: 'v',
    ...overrides,
  }
}

function summary(): SessionSummary {
  return {
    turnCount: 2,
    realtime: {
      turnCount: 1,
      avgSpeechEndToFirstAudioMs: 900,
      avgSpeechEndToPlaybackMs: 1100,
      estimatedCostPerMinuteUsd: 0.5,
      errorCount: 0,
      avgSttFinalMs: null,
      avgTranslationFinalMs: null,
      avgTtsFirstAudioMs: null,
    },
    cascade: {
      turnCount: 1,
      avgSpeechEndToFirstAudioMs: null, // cascade has no client→server latency channel (D.5/D.6)
      avgSpeechEndToPlaybackMs: null,
      estimatedCostPerMinuteUsd: 0.3,
      errorCount: 0,
      avgSttFinalMs: 120,
      avgTranslationFinalMs: 240,
      avgTtsFirstAudioMs: 360,
    },
    wer: { sampleCount: 1, avgWer: 0.25 },
    computedAt: '2026-05-30T00:00:00+00:00',
    pricingConfigVersion: 'v',
  }
}

function cost(model: string, perMin: number): CostEstimate {
  return {
    provider: 'openai',
    model,
    pricingBasis: 'tokens',
    estimatedUsd: 0,
    estimatedUsdPerMinute: perMin,
    units: {},
    pricingConfigVersion: 'v',
    assumptions: [],
  }
}

// The persisted wire turns carry `mode` + `costEstimate` (C# InterpretationTurn.CostEstimate) — NOT the
// frontend TurnViewModel's `cost`. The opaque InterpretationTurn shape is cast through `unknown`.
function sessionWithTurns(): InterpretationSession {
  return wireSession({
    turns: [
      { turnId: 't1', mode: 'cascade', costEstimate: cost('gpt-5-nano', 0.3) },
      { turnId: 't2', mode: 'realtime', costEstimate: cost('gpt-realtime', 0.5) },
    ] as unknown as InterpretationSession['turns'],
  })
}

describe('loadComparison', () => {
  it('loads the summary + session and aggregates per-variant cost from the persisted turns', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const api = {
      getSummary: vi.fn().mockResolvedValue(summary()),
      getSession: vi.fn().mockResolvedValue(sessionWithTurns()),
    }

    const result = await loadComparison({ store, api })

    expect(api.getSummary).toHaveBeenCalledWith('session_abc')
    expect(api.getSession).toHaveBeenCalledWith('session_abc')
    expect(result?.summary.turnCount).toBe(2)
    // The by-variant split is derived from the wire `costEstimate` field of each persisted turn.
    expect(result?.byVariant).toContainEqual({
      mode: 'cascade',
      model: 'gpt-5-nano',
      avgCostPerMinuteUsd: 0.3,
      turnCount: 1,
    })
    expect(result?.byVariant).toContainEqual({
      mode: 'realtime',
      model: 'gpt-realtime',
      avgCostPerMinuteUsd: 0.5,
      turnCount: 1,
    })
    expect(store.getState().errors).toEqual([])
  })

  it('extracts the per-mode models from the session providerProfile (cost-independent attribution)', async () => {
    // bug 6 (056): attribute the model per mode INDEPENDENT of cost (cost is absent — bug 5), so the
    // comparison cards + write-up can name models. cascade=translationModel, realtime=realtimeModel.
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const api = {
      getSummary: vi.fn().mockResolvedValue(summary()),
      getSession: vi.fn().mockResolvedValue(wireSession()),
    }

    const result = await loadComparison({ store, api })

    expect(result?.models).toEqual({ cascade: 'gpt-5-nano', realtime: 'gpt-realtime' })
  })

  it('routes a summary fetch error to the store and returns null (no headline → no comparison)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const uiError: UiError = {
      code: 'summary.load_failed',
      safeMessage: 'Could not load the session summary.',
      retryable: true,
    }
    const api = {
      getSummary: vi.fn().mockRejectedValue(new ApiError(uiError)),
      getSession: vi.fn(),
    }

    const result = await loadComparison({ store, api })

    expect(result).toBeNull()
    expect(store.getState().errors).toContainEqual(uiError)
    expect(api.getSession).not.toHaveBeenCalled() // no point fetching the session without the headline
  })

  it('degrades the per-variant breakdown to null when the session fetch fails — the per-mode summary survives', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const uiError: UiError = {
      code: 'session.load_failed',
      safeMessage: 'Could not load the per-variant cost breakdown.',
      retryable: true,
    }
    const api = {
      getSummary: vi.fn().mockResolvedValue(summary()),
      getSession: vi.fn().mockRejectedValue(new ApiError(uiError)),
    }

    const result = await loadComparison({ store, api })

    expect(result?.summary.turnCount).toBe(2) // per-mode headline still renders
    expect(result?.byVariant).toBeNull() // the variant source degraded independently
    expect(result?.models).toBeNull() // models share the getSession source → also null on its failure
    expect(store.getState().errors).toContainEqual(uiError)
  })

  it('returns null and calls no api when there is no active session', async () => {
    const store = createSessionStore() // sessionId null — never sessionStarted
    const api = { getSummary: vi.fn(), getSession: vi.fn() }

    const result = await loadComparison({ store, api })

    expect(result).toBeNull()
    expect(api.getSummary).not.toHaveBeenCalled()
    expect(api.getSession).not.toHaveBeenCalled()
  })

  it('synthesizes a fixed-message error on a non-ApiError summary throw (no raw leak)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const api = {
      getSummary: vi.fn().mockRejectedValue(new Error('boom-internal-detail')),
      getSession: vi.fn(),
    }

    const result = await loadComparison({ store, api })

    expect(result).toBeNull()
    const errors = store.getState().errors
    expect(errors).toHaveLength(1)
    expect(errors[0].safeMessage).not.toContain('boom-internal-detail')
    expect(api.getSession).not.toHaveBeenCalled()
  })
})
