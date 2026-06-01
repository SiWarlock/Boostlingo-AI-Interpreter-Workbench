import { describe, expect, it, vi } from 'vitest'
import { evaluateFromBlob } from './evaluationActions'
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

// evaluateFromBlob is the push-to-talk evaluate core (096): the panel owns the recording lifecycle
// (startBlobRecording → stop → blob) and hands the captured blob here. Sequence:
//   zero-byte guard → transcribe (STT-only) → no-speech guard → create eval turn → compute + PERSIST WER
// DI'd + unit-tested against the real store + mocked api (lesson §7); no capture dep (the panel records).

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

type Deps = Parameters<typeof evaluateFromBlob>[0]

function makeDeps(store: SessionStore, over: Partial<Deps> = {}): Deps {
  return {
    store,
    api: {
      transcribe: vi.fn().mockResolvedValue(transcribed),
      computeWer: vi.fn().mockResolvedValue({ result: werResult() } satisfies WerResponse),
    },
    createTurn: vi.fn().mockResolvedValue({ turnId: 'turn_eval_1' }),
    ...over,
  }
}

const input = { phraseId: 'p1', language: 'en' } as const
const blob = new Blob(['x'], { type: 'audio/webm' })

describe('evaluateFromBlob', () => {
  it('returns null and makes no api calls when there is no active session', async () => {
    const store = createSessionStore() // sessionId null — never sessionStarted
    const deps = makeDeps(store)

    const result = await evaluateFromBlob(deps, blob, input)

    expect(result).toBeNull()
    expect(deps.api.transcribe).not.toHaveBeenCalled()
    expect(deps.createTurn).not.toHaveBeenCalled()
    expect(deps.api.computeWer).not.toHaveBeenCalled()
  })

  it('transcribes, creates an eval turn, scores WER, and returns a scored outcome', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const deps = makeDeps(store)

    const result = await evaluateFromBlob(deps, blob, input)

    expect(deps.api.transcribe).toHaveBeenCalledWith(
      { sessionId: 'session_abc', phraseId: 'p1', language: 'en' },
      expect.any(Blob),
    )
    expect(deps.createTurn).toHaveBeenCalledWith('session_abc')
    expect(deps.api.computeWer).toHaveBeenCalledTimes(1)

    // Ordering: transcribe -> createTurn -> computeWer (don't score a failed transcribe; create the eval
    // turn only once a hypothesis exists).
    const order = [
      vi.mocked(deps.api.transcribe).mock.invocationCallOrder[0],
      vi.mocked(deps.createTurn).mock.invocationCallOrder[0],
      vi.mocked(deps.api.computeWer).mock.invocationCallOrder[0],
    ]
    expect(order).toEqual([...order].sort((a, b) => a - b))

    expect(result).toEqual({ kind: 'scored', hypothesis: 'the quick fox', werResult: werResult() })
    expect(store.getState().errors).toEqual([])
  })

  it('returns a no-speech outcome when the hypothesis is empty/whitespace — never a fabricated score (Finding 3)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    // The STT heard nothing (the live-repro symptom: user spoke late / not at all). An empty hypothesis
    // scored against a reference = all-deletions = a misleading "100%". The honest degrade: a DISTINCT
    // no-speech outcome, NO eval turn created, NO WER computed, NO error surfaced (it's a valid result).
    const deps = makeDeps(store, {
      api: {
        transcribe: vi.fn().mockResolvedValue({ ...transcribed, hypothesis: '   ' }),
        computeWer: vi.fn(),
      },
    })

    const result = await evaluateFromBlob(deps, blob, input)

    expect(result).toEqual({ kind: 'no-speech' })
    expect(deps.createTurn).not.toHaveBeenCalled()
    expect(deps.api.computeWer).not.toHaveBeenCalled()
    expect(store.getState().errors).toEqual([]) // no-speech is an outcome, not an error
  })

  it('calls computeWer WITH the created turnId — the persist path F.3 depends on', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const deps = makeDeps(store)

    await evaluateFromBlob(deps, blob, input)

    expect(deps.api.computeWer).toHaveBeenCalledWith({
      sessionId: 'session_abc',
      turnId: 'turn_eval_1',
      phraseId: 'p1',
      hypothesis: 'the quick fox',
    })
  })

  it('surfaces a persistence warning to the store but still returns the scored outcome', async () => {
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

    const result = await evaluateFromBlob(deps, blob, input)

    expect(store.getState().errors).toContainEqual(warning)
    expect(result).toEqual({ kind: 'scored', hypothesis: 'the quick fox', werResult: werResult() })
  })

  it('aborts on a zero-byte blob — never POSTs empty audio to the paid /transcribe (060 guard preserved)', async () => {
    const store = createSessionStore()
    store.sessionStarted(wireSession())
    const emptyBlob = new Blob([], { type: 'audio/webm' }) // size === 0
    const deps = makeDeps(store)

    const result = await evaluateFromBlob(deps, emptyBlob, input)

    expect(result).toBeNull()
    expect(deps.api.transcribe).not.toHaveBeenCalled()
    expect(deps.createTurn).not.toHaveBeenCalled()
    expect(deps.api.computeWer).not.toHaveBeenCalled()
    const errors = store.getState().errors
    expect(errors).toHaveLength(1)
    expect(errors[0].code).toBe('capture.empty') // distinct, actionable "nothing recorded" code
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

    const result = await evaluateFromBlob(deps, blob, input)

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

    const result = await evaluateFromBlob(deps, blob, input)

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

    const result = await evaluateFromBlob(deps, blob, input)

    expect(result).toBeNull()
    const errors = store.getState().errors
    expect(errors).toHaveLength(1)
    expect(errors[0].safeMessage).not.toContain('boom-internal-detail')
    expect(deps.api.computeWer).not.toHaveBeenCalled()
  })
})
