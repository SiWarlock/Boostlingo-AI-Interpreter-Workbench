// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Mock the api client (phrase load), the DI'd flow (evaluateFromBlob), and the capture seam
// (startBlobRecording). The panel is a thin render+dispatch shell over these — no transport internals
// here (ARCH-007). Push-to-talk: Record → countdown → recording → Stop → evaluate (096).
vi.mock('../api/evaluationApi', () => ({
  evaluationApi: {
    getPhrases: vi.fn(),
    transcribe: vi.fn(),
    computeWer: vi.fn(),
  },
}))
vi.mock('../state/evaluationActions', () => ({
  evaluateFromBlob: vi.fn(),
}))
vi.mock('../audio/audioCaptureController', () => ({
  audioCaptureController: { startBlobRecording: vi.fn() },
}))

import EvaluationPanel from './EvaluationPanel'
import { evaluationApi } from '../api/evaluationApi'
import { evaluateFromBlob } from '../state/evaluationActions'
import { audioCaptureController } from '../audio/audioCaptureController'
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
        translationModel: 'gpt-5-nano',
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

const scoredResult: WerResult = {
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

// Flush pending microtasks (phrase load, startBlobRecording/stop resolves, evaluateFromBlob) inside act.
async function flush(): Promise<void> {
  await act(async () => {
    await Promise.resolve()
    await Promise.resolve()
    await Promise.resolve()
  })
}

afterEach(() => {
  cleanup()
  sessionStore.reset()
  vi.clearAllMocks()
  vi.useRealTimers()
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

  it('disables the Record button and shows a hint when there is no active session', async () => {
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    // No sessionStarted → sessionId null, the gate is closed.

    render(<EvaluationPanel />)
    await screen.findByText('the quick brown fox')

    expect(screen.getByRole('button', { name: /record/i })).toBeDisabled()
    expect(screen.getByText(/start a session to evaluate/i)).toBeInTheDocument()

    // Clicking a disabled button is a no-op in the DOM → no recording is ever started.
    fireEvent.click(screen.getByRole('button', { name: /record/i }))
    expect(audioCaptureController.startBlobRecording).not.toHaveBeenCalled()
  })

  it('push-to-talk: Record → countdown → recording (visible status + Stop button), and starts the capture seam', async () => {
    vi.useFakeTimers()
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    vi.mocked(audioCaptureController.startBlobRecording).mockResolvedValue({
      stop: vi.fn().mockResolvedValue({
        blob: new Blob(['x'], { type: 'audio/webm' }),
        mimeType: 'audio/webm',
      }),
    })
    sessionStore.sessionStarted(activeSession())

    render(<EvaluationPanel />)
    await flush() // phrase load

    fireEvent.click(screen.getByRole('button', { name: /record/i }))
    // Countdown lead-in visible BEFORE recording — answers the "it never told me when" complaint.
    expect(screen.getByRole('status')).toHaveTextContent(/get ready/i)
    // The capture seam must NOT open the mic until the countdown completes.
    expect(audioCaptureController.startBlobRecording).not.toHaveBeenCalled()

    await act(async () => {
      vi.advanceTimersByTime(3000) // run the 3·2·1 countdown to completion
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(audioCaptureController.startBlobRecording).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('status')).toHaveTextContent(/recording/i)
    expect(screen.getByRole('button', { name: /stop/i })).toBeInTheDocument()
  })

  it('push-to-talk: clicking Stop evaluates the captured blob and renders the scored WER result', async () => {
    vi.useFakeTimers()
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    const stop = vi
      .fn()
      .mockResolvedValue({ blob: new Blob(['x'], { type: 'audio/webm' }), mimeType: 'audio/webm' })
    vi.mocked(audioCaptureController.startBlobRecording).mockResolvedValue({ stop })
    vi.mocked(evaluateFromBlob).mockResolvedValue({
      kind: 'scored',
      hypothesis: 'the quick fox',
      werResult: scoredResult,
    })
    sessionStore.sessionStarted(activeSession())

    render(<EvaluationPanel />)
    await flush()
    fireEvent.click(screen.getByRole('button', { name: /record/i }))
    await act(async () => {
      vi.advanceTimersByTime(3000)
      await Promise.resolve()
      await Promise.resolve()
    })

    fireEvent.click(screen.getByRole('button', { name: /stop/i }))
    await flush()

    expect(stop).toHaveBeenCalledTimes(1)
    expect(evaluateFromBlob).toHaveBeenCalledTimes(1)
    expect(screen.getByText(/25\.0%/)).toBeInTheDocument() // WER percentage
    expect(screen.getByText(/Deletions:\s*1/i)).toBeInTheDocument() // S/I/D breakdown
    expect(screen.getByText('the quick fox')).toBeInTheDocument() // the STT hypothesis
  })

  it('no-speech outcome renders "No speech detected — n/a" and NEVER a percentage', async () => {
    vi.useFakeTimers()
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    const stop = vi
      .fn()
      .mockResolvedValue({ blob: new Blob(['x'], { type: 'audio/webm' }), mimeType: 'audio/webm' })
    vi.mocked(audioCaptureController.startBlobRecording).mockResolvedValue({ stop })
    vi.mocked(evaluateFromBlob).mockResolvedValue({ kind: 'no-speech' })
    sessionStore.sessionStarted(activeSession())

    render(<EvaluationPanel />)
    await flush()
    fireEvent.click(screen.getByRole('button', { name: /record/i }))
    await act(async () => {
      vi.advanceTimersByTime(3000)
      await Promise.resolve()
      await Promise.resolve()
    })
    fireEvent.click(screen.getByRole('button', { name: /stop/i }))
    await flush()

    expect(screen.getByText(/no speech detected/i)).toBeInTheDocument()
    // The whole point of Finding 3: a silent/empty capture must NOT render a confident score.
    expect(screen.queryByText(/%/)).not.toBeInTheDocument()
  })

  it('after a result, Record is available again and a second evaluation runs without reload', async () => {
    vi.useFakeTimers()
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    const stop = vi
      .fn()
      .mockResolvedValue({ blob: new Blob(['x'], { type: 'audio/webm' }), mimeType: 'audio/webm' })
    vi.mocked(audioCaptureController.startBlobRecording).mockResolvedValue({ stop })
    vi.mocked(evaluateFromBlob).mockResolvedValue({ kind: 'no-speech' })
    sessionStore.sessionStarted(activeSession())

    render(<EvaluationPanel />)
    await flush()

    // First cycle → a result (no-speech).
    fireEvent.click(screen.getByRole('button', { name: /record/i }))
    await act(async () => {
      vi.advanceTimersByTime(3000)
      await Promise.resolve()
      await Promise.resolve()
    })
    fireEvent.click(screen.getByRole('button', { name: /stop/i }))
    await flush()
    expect(screen.getByText(/no speech detected/i)).toBeInTheDocument()

    // Retry: Record is available again → a second recording starts without a reload.
    fireEvent.click(screen.getByRole('button', { name: /record/i }))
    await act(async () => {
      vi.advanceTimersByTime(3000)
      await Promise.resolve()
      await Promise.resolve()
    })
    expect(audioCaptureController.startBlobRecording).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('status')).toHaveTextContent(/recording/i)
    expect(screen.getByRole('button', { name: /stop/i })).toBeInTheDocument()
  })

  it('mic-fail (startBlobRecording returns null) resets to idle and surfaces capture.failed', async () => {
    vi.useFakeTimers()
    vi.mocked(evaluationApi.getPhrases).mockResolvedValue(phrases)
    vi.mocked(audioCaptureController.startBlobRecording).mockResolvedValue(null) // mic denied / unsupported
    sessionStore.sessionStarted(activeSession())

    render(<EvaluationPanel />)
    await flush()
    fireEvent.click(screen.getByRole('button', { name: /record/i }))
    await act(async () => {
      vi.advanceTimersByTime(3000)
      await Promise.resolve()
      await Promise.resolve()
    })

    // Back to idle (Record button visible again, no Stop, no recording status), with the sanitized error.
    expect(screen.getByRole('button', { name: /record/i })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /stop/i })).not.toBeInTheDocument()
    expect(sessionStore.getState().errors.some((e) => e.code === 'capture.failed')).toBe(true)
  })
})
