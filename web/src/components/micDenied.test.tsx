// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import RecordingControls from './RecordingControls'
import ErrorBanner from './ErrorBanner'
import { sessionStore } from '../state/sessionStore'
import type { InterpretationSession } from '../types/domain'

// PRD/ARCH-020 transition #2, held to a bar: a getUserMedia rejection (mic denied) drives the REAL
// recording path (D.4b recordingActions -> D.3 capture controller -> micErrorToUiError) and surfaces an
// actionable mic-permission error in the ErrorBanner — without leaving the UI stuck mid-record. Per-file
// jsdom env (Q1). createTurn (fetch) is stubbed so the flow reaches the capture step; getUserMedia is
// mocked to reject BEFORE AudioContext is touched, so no browser audio realm is needed.

const wireSession: InterpretationSession = {
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

afterEach(() => {
  cleanup()
  sessionStore.reset()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('mic-denied — getUserMedia rejection (ARCH-020)', () => {
  it('surfaces an actionable mic-permission error and does not get stuck recording (Stop disabled, retry possible)', async () => {
    sessionStore.reset()
    sessionStore.sessionStarted(wireSession) // active session, turnStatus 'ready' -> Start enabled

    // createTurn must succeed so the flow reaches the capture step (where the mic is denied). Mint a
    // FRESH Response per call (lesson §4 — a body is single-read; mockResolvedValue would reuse one).
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(
        () =>
          new Response(JSON.stringify({ turnId: 'turn_x' }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    )

    // getUserMedia denied (NotAllowedError) — the real micErrorToUiError maps this to mic.permission_denied.
    // Stub via vi.stubGlobal so afterEach's vi.unstubAllGlobals() restores navigator (symmetric teardown).
    const getUserMedia = vi
      .fn()
      .mockRejectedValue(Object.assign(new Error('denied'), { name: 'NotAllowedError' }))
    vi.stubGlobal('navigator', { ...navigator, mediaDevices: { getUserMedia } })

    render(
      <>
        <RecordingControls />
        <ErrorBanner />
      </>,
    )

    const start = screen.getByRole('button', { name: /start recording/i })
    expect(start).toBeEnabled()
    fireEvent.click(start)

    // the actionable mic copy appears (driven through the real capture path)
    await waitFor(() =>
      expect(screen.getByText(/microphone permission denied/i)).toBeInTheDocument(),
    )
    expect(getUserMedia).toHaveBeenCalled() // the real capture path actually ran

    // not stuck mid-record: Stop is disabled; Start stays enabled so the user can grant + retry
    // (matches the actionable "...and retry" copy — turnStatus is 'failed', which canStartRecording allows).
    expect(screen.getByRole('button', { name: /stop/i })).toBeDisabled()
    expect(screen.getByRole('button', { name: /start recording/i })).toBeEnabled()
  })
})
