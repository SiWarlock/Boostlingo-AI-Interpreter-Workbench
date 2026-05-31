// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Mock the connection manager (assert teardown dispatch) + sessionActions (avoid the real endSession fetch).
vi.mock('../realtime/realtimeConnectionManager', () => ({
  realtimeConnectionManager: {
    teardown: vi.fn(),
    ensureConnected: vi.fn().mockResolvedValue(undefined),
  },
}))
vi.mock('../state/sessionActions', () => ({
  endSession: vi.fn().mockResolvedValue(undefined),
  startSession: vi.fn().mockResolvedValue(undefined),
}))

import SessionSetup from './SessionSetup'
import { realtimeConnectionManager } from '../realtime/realtimeConnectionManager'
import { endSession } from '../state/sessionActions'
import { sessionStore } from '../state/sessionStore'
import type { ConfigResponse, InterpretationSession } from '../types/domain'

const config: ConfigResponse = {
  realtime: { configured: true, models: ['gpt-realtime', 'gpt-realtime-mini'] },
  cascade: {
    stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
    translation: { configured: true, provider: 'openai', models: ['gpt-5-nano', 'gpt-5-mini'] },
    tts: { configured: true, provider: 'openai', model: 'gpt-4o-mini-tts' },
  },
  languages: ['en', 'es'],
  pricingConfigVersion: 'v',
}

const session: InterpretationSession = {
  sessionId: 'session_abc',
  startedAt: '2026-05-29T12:00:00+00:00',
  config: {
    currentMode: 'realtime',
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
  vi.clearAllMocks()
})

describe('SessionSetup — turn-control toggle (Manual | Auto-VAD) (I.2 slice 1)', () => {
  it('renders Manual|Auto, KEEPS Manual, and dispatches setTurnControlMode on Auto', () => {
    sessionStore.reset()
    sessionStore.loadConfig(config)
    render(<SessionSetup />)

    // both options present (Manual kept — §22 adjust-a-query-never-an-assertion); default is manual
    expect(screen.getByRole('button', { name: /manual/i })).toBeInTheDocument()
    const auto = screen.getByRole('button', { name: /auto/i })
    expect(sessionStore.getState().turnControlMode).toBe('manual')

    fireEvent.click(auto)

    expect(sessionStore.getState().turnControlMode).toBe('auto')
  })

  it('disables the turn-control toggle while a turn is in flight (recording/processing/playing — canToggleMode)', () => {
    for (const status of ['recording', 'processing', 'playing'] as const) {
      sessionStore.reset()
      sessionStore.loadConfig(config)
      sessionStore.sessionStarted(session)
      sessionStore.setTurnStatus(status) // mid-turn → gated (covers all TOGGLE_BLOCKED statuses)
      const { unmount } = render(<SessionSetup />)

      expect(screen.getByRole('button', { name: /manual/i })).toBeDisabled()
      expect(screen.getByRole('button', { name: /auto/i })).toBeDisabled()
      unmount()
    }
  })
})

describe('SessionSetup — End session teardown (E.5a)', () => {
  it('tears down the realtime connection when the session ends', () => {
    sessionStore.loadConfig(config)
    sessionStore.sessionStarted(session) // active -> End enabled

    render(<SessionSetup />)
    fireEvent.click(screen.getByRole('button', { name: /end session/i }))

    expect(endSession).toHaveBeenCalledTimes(1) // the End flow still ends the session...
    expect(realtimeConnectionManager.teardown).toHaveBeenCalledTimes(1) // ...and tears down the realtime pc
  })
})
