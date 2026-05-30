// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Mock both turn controllers so the test asserts DISPATCH-BY-MODE, not the controllers' internals.
vi.mock('../state/recordingActions', () => ({
  recordingController: { startRecording: vi.fn().mockResolvedValue(undefined), stopRecording: vi.fn() },
}))
vi.mock('../realtime/realtimeTurnController', () => ({
  realtimeTurnController: { startTurn: vi.fn().mockResolvedValue(undefined), stopTurn: vi.fn() },
}))

import RecordingControls from './RecordingControls'
import { recordingController } from '../state/recordingActions'
import { realtimeTurnController } from '../realtime/realtimeTurnController'
import { sessionStore } from '../state/sessionStore'
import type { InterpretationMode, InterpretationSession } from '../types/domain'

function session(mode: InterpretationMode): InterpretationSession {
  return {
    sessionId: 'session_abc',
    startedAt: '2026-05-29T12:00:00+00:00',
    config: {
      currentMode: mode,
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

describe('RecordingControls — dispatch by mode', () => {
  it('dispatches Start to the realtime controller when mode is realtime', () => {
    sessionStore.sessionStarted(session('realtime'))

    render(<RecordingControls />)
    fireEvent.click(screen.getByRole('button', { name: /start recording/i }))

    expect(realtimeTurnController.startTurn).toHaveBeenCalledTimes(1)
    expect(recordingController.startRecording).not.toHaveBeenCalled()
  })

  it('dispatches Start to the cascade controller when mode is cascade', () => {
    sessionStore.sessionStarted(session('cascade'))

    render(<RecordingControls />)
    fireEvent.click(screen.getByRole('button', { name: /start recording/i }))

    expect(recordingController.startRecording).toHaveBeenCalledTimes(1)
    expect(realtimeTurnController.startTurn).not.toHaveBeenCalled()
  })

  it('dispatches Stop to the realtime controller when mode is realtime', () => {
    sessionStore.sessionStarted(session('realtime'))
    sessionStore.setTurnStatus('recording') // enable Stop

    render(<RecordingControls />)
    fireEvent.click(screen.getByRole('button', { name: /stop/i }))

    expect(realtimeTurnController.stopTurn).toHaveBeenCalledTimes(1)
    expect(recordingController.stopRecording).not.toHaveBeenCalled()
  })

  it('dispatches Stop to the cascade controller when mode is cascade', () => {
    sessionStore.sessionStarted(session('cascade'))
    sessionStore.setTurnStatus('recording')

    render(<RecordingControls />)
    fireEvent.click(screen.getByRole('button', { name: /stop/i }))

    expect(recordingController.stopRecording).toHaveBeenCalledTimes(1)
    expect(realtimeTurnController.stopTurn).not.toHaveBeenCalled()
  })
})
