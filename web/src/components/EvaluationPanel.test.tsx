// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Mock the api client (phrase load) + the DI'd flow (assert dispatch / drive the result render). The
// panel is a thin render+dispatch shell over these — no transport internals here (ARCH-007).
vi.mock('../api/evaluationApi', () => ({
  evaluationApi: {
    getPhrases: vi.fn(),
    transcribe: vi.fn(),
    computeWer: vi.fn(),
  },
}))
vi.mock('../state/evaluationActions', () => ({
  runEvaluation: vi.fn(),
}))

import EvaluationPanel from './EvaluationPanel'
import { evaluationApi } from '../api/evaluationApi'
import { runEvaluation } from '../state/evaluationActions'
import { sessionStore } from '../state/sessionStore'
import type { EvaluationPhrase, InterpretationSession, WerResult } from '../types/domain'

const phrases: EvaluationPhrase[] = [
  { phraseId: 'p1', language: 'en', referenceText: 'the quick brown fox', category: 'pangram' },
  { phraseId: 'p2', language: 'es', referenceText: 'el veloz murcielago', category: 'pangram' },
]

function activeSession(): InterpretationSession {
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
    pricingConfigVersion: 'v',
  }
}

afterEach(() => {
  cleanup()
  sessionStore.reset()
  vi.clearAllMocks()
})

describe('EvaluationPanel', () => {
  it('loads phrases on mount and renders the selector + the selected reference text', async () => {
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)

    render(<EvaluationPanel />)

    // The first phrase is selected by default → its reference text renders once phrases load.
    expect(await screen.findByText('the quick brown fox')).toBeInTheDocument()
    expect(screen.getByRole('combobox')).toBeInTheDocument() // the phrase <select>
  })

  it('renders the "WER is STT-only" explanation verbatim (ARCH-015)', async () => {
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)

    render(<EvaluationPanel />)

    expect(
      await screen.findByText(
        /It is useful for STT quality, not a full measure of translation quality\./i,
      ),
    ).toBeInTheDocument()
  })

  it('dispatches the evaluation flow when the record button is clicked (active session)', async () => {
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    vi.mocked(runEvaluation).mockResolvedValue(null)
    sessionStore.sessionStarted(activeSession()) // active + sessionId → the gate opens

    render(<EvaluationPanel />)
    await screen.findByText('the quick brown fox')

    fireEvent.click(screen.getByRole('button', { name: /record/i }))

    expect(runEvaluation).toHaveBeenCalledTimes(1)
  })

  it('disables the record button and shows a hint when there is no active session', async () => {
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    // No sessionStarted → sessionId null, the gate is closed.

    render(<EvaluationPanel />)
    await screen.findByText('the quick brown fox')

    expect(screen.getByRole('button', { name: /record/i })).toBeDisabled()
    expect(screen.getByText(/start a session to evaluate/i)).toBeInTheDocument()

    // Clicking a disabled button is a no-op in the DOM → the flow is never dispatched.
    fireEvent.click(screen.getByRole('button', { name: /record/i }))
    expect(runEvaluation).not.toHaveBeenCalled()
  })

  it('renders the WER result (wer %, S/I/D/N, hypothesis) after a successful evaluation', async () => {
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    const result: WerResult = {
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
    }
    vi.mocked(runEvaluation).mockResolvedValue({ hypothesis: 'the quick fox', werResult: result })
    sessionStore.sessionStarted(activeSession())

    render(<EvaluationPanel />)
    await screen.findByText('the quick brown fox')
    fireEvent.click(screen.getByRole('button', { name: /record/i }))

    expect(await screen.findByText(/25\.0%/)).toBeInTheDocument() // WER percentage
    expect(screen.getByText(/Deletions:\s*1/i)).toBeInTheDocument() // S/I/D/N breakdown
    expect(screen.getByText('the quick fox')).toBeInTheDocument() // the STT hypothesis
  })
})
