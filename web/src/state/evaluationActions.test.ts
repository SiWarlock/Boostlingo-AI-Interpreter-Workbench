import { describe, expect, it, vi } from 'vitest'
import { runEvaluation } from './evaluationActions'
import { createSessionStore } from './sessionStore'
import type { SessionStore } from './sessionStore'
import { ApiError } from '../api/http'
import type {
  InterpretationSession,
  TranscribeResponse,
  UiError,
  WerResponse,
  WerResult,
} from '../types/domain'

// A live, active session is the precondition (the flow reads sessionId from the store + creates a turn).
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
        translationModel: 'gpt-5.4-nano',
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

function werResult(overrides: Partial<WerResult> = {}): WerResult {
  return {
    phraseId: 'p1',
    reference: 'the quick brown fox',
    hypothesis: 'the quick fox',
    normalizedReference: 'the quick brown fox',
    normalizedHypothesis: 'the quick fox',
    substitutions: 0,
    insertions: 0,
    deletions: 1,
    referenceWordCount: 4,
    wer: 0.25,
    ...overrides,
  }
}

const transcribed: TranscribeResponse = {
  hypothesis: 'the quick fox',
  sttProvider: 'deepgram',
  sttModel: 'nova-3',
  latencyEvents: [],
}

type Deps = Parameters<typeof runEvaluation>[0]

function makeDeps(store: SessionStore, over: Partial<Deps> = {}): Deps {
  const blob = new Blob(['x'], { type: 'audio/webm' })
  return {
    store,
    api: {
      transcribe: vi.fn().mockResolvedValue(transcribed),
      computeWer: vi.fn().mockResolvedValue({ result: werResult() } satisfies WerResponse),
    },
    createTurn: vi.fn().mockResolvedValue({ turnId: 'turn_eval_1' }),
    capture: { recordBlob: vi.fn().mockResolvedValue({ blob, mimeType: 'audio/webm' }) },
    ...over,
  }
}

const input = { phraseId: 'p1', language: 'en' } as const

describe('runEvaluation', () => {
  it('returns null and makes no api/capture calls when there is no active session', async () => {
    const store = createSessionStore() // sessionId null — never sessionStarted
    const deps = makeDeps(store)

    const result = await runEvaluation(deps, input)

    expect(result).toBeNull()
    expect(deps.capture.recordBlob).not.toHaveBeenCalled()
    expect(deps.api.transcribe).not.toHaveBeenCalled()
    expect(deps.createTurn).not.toHaveBeenCalled()
    expect(deps.api.computeWer).not.toHaveBeenCalled()
  })

  it('records, transcribes, creates an eval turn, scores WER, and returns the outcome', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const deps = makeDeps(store)

    const result = await runEvaluation(deps, input)

    expect(deps.capture.recordBlob).toHaveBeenCalledTimes(1)
    expect(deps.api.transcribe).toHaveBeenCalledWith(
      { sessionId: 'session_abc', phraseId: 'p1', language: 'en' },
      expect.any(Blob),
    )
    expect(deps.createTurn).toHaveBeenCalledWith('session_abc')
    expect(deps.api.computeWer).toHaveBeenCalledTimes(1)

    // Ordering: record -> transcribe -> createTurn -> computeWer (don't score a failed transcribe;
    // create the eval turn only once a hypothesis exists).
    const order = [
      vi.mocked(deps.capture.recordBlob).mock.invocationCallOrder[0],
      vi.mocked(deps.api.transcribe).mock.invocationCallOrder[0],
      vi.mocked(deps.createTurn).mock.invocationCallOrder[0],
      vi.mocked(deps.api.computeWer).mock.invocationCallOrder[0],
    ]
    expect(order).toEqual([...order].sort((a, b) => a - b))

    expect(result).toEqual({ hypothesis: 'the quick fox', werResult: werResult() })
    expect(store.getState().errors).toEqual([])
  })

  it('calls computeWer WITH the created turnId — the persist path F.3 depends on', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const deps = makeDeps(store)

    await runEvaluation(deps, input)

    expect(deps.api.computeWer).toHaveBeenCalledWith({
      sessionId: 'session_abc',
      turnId: 'turn_eval_1',
      phraseId: 'p1',
      hypothesis: 'the quick fox',
    })
  })

  it('surfaces a persistence warning to the store but still returns the outcome', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const warning: UiError = {
      code: 'persistence.failed',
      safeMessage: 'The score may not have been saved.',
      retryable: false,
    }
    const deps = makeDeps(store, {
      api: {
        transcribe: vi.fn().mockResolvedValue(transcribed),
        computeWer: vi.fn().mockResolvedValue({
          result: werResult(),
          persistenceWarning: warning,
        } satisfies WerResponse),
      },
    })

    const result = await runEvaluation(deps, input)

    expect(store.getState().errors).toContainEqual(warning)
    expect(result).toEqual({ hypothesis: 'the quick fox', werResult: werResult() })
  })

  it('routes a transcribe ApiError to the store and aborts before createTurn/computeWer', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const uiError: UiError = {
      code: 'evaluation.invalid_request',
      safeMessage: 'The request was invalid.',
      retryable: false,
    }
    const deps = makeDeps(store, {
      api: {
        transcribe: vi.fn().mockRejectedValue(new ApiError(uiError)),
        computeWer: vi.fn(),
      },
    })

    const result = await runEvaluation(deps, input)

    expect(result).toBeNull()
    expect(store.getState().errors).toContainEqual(uiError)
    expect(deps.createTurn).not.toHaveBeenCalled()
    expect(deps.api.computeWer).not.toHaveBeenCalled()
  })

  it('routes a createTurn ApiError to the store and aborts before computeWer (transcribe already succeeded)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const uiError: UiError = {
      code: 'turn.create_failed',
      safeMessage: 'Could not create an evaluation turn.',
      retryable: true,
    }
    const deps = makeDeps(store, {
      createTurn: vi.fn().mockRejectedValue(new ApiError(uiError)),
    })

    const result = await runEvaluation(deps, input)

    expect(result).toBeNull()
    expect(deps.api.transcribe).toHaveBeenCalledTimes(1) // transcribe succeeded...
    expect(store.getState().errors).toContainEqual(uiError)
    // ...but we don't score/persist against a turn that never got created.
    expect(deps.api.computeWer).not.toHaveBeenCalled()
  })

  it('on a non-ApiError throw synthesizes a fixed-message error without leaking the raw message', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const deps = makeDeps(store, {
      api: {
        transcribe: vi.fn().mockRejectedValue(new Error('boom-internal-detail')),
        computeWer: vi.fn(),
      },
    })

    const result = await runEvaluation(deps, input)

    expect(result).toBeNull()
    const errors = store.getState().errors
    expect(errors).toHaveLength(1)
    expect(errors[0].safeMessage).not.toContain('boom-internal-detail')
    expect(deps.api.computeWer).not.toHaveBeenCalled()
  })

  it('aborts when recordBlob returns null (mic denied / capture failed) and surfaces a capture error', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const deps = makeDeps(store, {
      capture: { recordBlob: vi.fn().mockResolvedValue(null) },
    })

    const result = await runEvaluation(deps, input)

    expect(result).toBeNull()
    expect(deps.api.transcribe).not.toHaveBeenCalled()
    expect(deps.createTurn).not.toHaveBeenCalled()
    expect(deps.api.computeWer).not.toHaveBeenCalled()
    // recordBlob returns null silently (no onError on the blob path) — the flow surfaces its own error.
    expect(store.getState().errors).toHaveLength(1)
  })
})
