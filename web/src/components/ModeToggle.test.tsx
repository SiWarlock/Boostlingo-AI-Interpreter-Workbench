// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Mock the connection manager so the mode-switch-away test asserts the teardown DISPATCH (E.5b), not the
// manager internals. Harmless to the disabled-state test below (it never clicks).
vi.mock('../realtime/realtimeConnectionManager', () => ({
  realtimeConnectionManager: { onModeSwitch: vi.fn(), teardown: vi.fn(), ensureConnected: vi.fn() },
}))
// Mock the sessions api (only setMode is exercised here — brief 050). The REAL switchMode flow runs
// against it (we test the wiring, not the flow internals — those are unit-tested in sessionActions.test).
vi.mock('../api/sessionsApi', () => ({ sessionsApi: { setMode: vi.fn() } }))

import ModeToggle from './ModeToggle'
import { realtimeConnectionManager } from '../realtime/realtimeConnectionManager'
import { sessionsApi } from '../api/sessionsApi'
import { sessionStore } from '../state/sessionStore'
import type { ConfigResponse, InterpretationSession, TurnStatus } from '../types/domain'

// PRD/ARCH-020 transition #1, held to a bar at the render level: the mode toggle is disabled while a
// turn is in flight (recording/processing/playing) and enabled otherwise. Exercises D.2's ModeToggle +
// canToggleMode end-to-end through React. Per-file jsdom env (Q1) so the node-env unit suite is untouched.

const fullConfig: ConfigResponse = {
  realtime: { configured: true, models: ['gpt-realtime', 'gpt-realtime-mini'] },
  cascade: {
    stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
    translation: { configured: true, provider: 'openai', models: ['gpt-5-nano', 'gpt-5-mini'] },
    tts: { configured: true, provider: 'openai', model: 'gpt-4o-mini-tts' },
  },
  languages: ['en', 'es'],
  pricingConfigVersion: 'v',
}

afterEach(() => {
  cleanup()
  sessionStore.reset()
  vi.clearAllMocks()
})

function session(mode: InterpretationSession['config']['currentMode']): InterpretationSession {
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

describe('ModeToggle — mode-toggle-disabled-during-active-turn (ARCH-020)', () => {
  it('disables both mode buttons during recording/processing/playing, enables them when idle/done', () => {
    sessionStore.reset()
    sessionStore.loadConfig(fullConfig) // both modes available -> gating is driven only by canToggleMode
    render(<ModeToggle />)

    const cascade = screen.getByRole('button', { name: 'Cascade' })
    const realtime = screen.getByRole('button', { name: 'Realtime' })

    act(() => sessionStore.setTurnStatus('ready'))
    expect(cascade).toBeEnabled()
    expect(realtime).toBeEnabled()

    for (const status of ['recording', 'processing', 'playing'] as TurnStatus[]) {
      act(() => sessionStore.setTurnStatus(status))
      expect(cascade).toBeDisabled()
      expect(realtime).toBeDisabled()
    }

    act(() => sessionStore.setTurnStatus('completed'))
    expect(cascade).toBeEnabled()
    expect(realtime).toBeEnabled()
  })
})

describe('ModeToggle — Flow-G mode-switch teardown (E.5b)', () => {
  it('invokes the realtime teardown handler when switching realtime -> cascade', () => {
    sessionStore.reset()
    sessionStore.loadConfig(fullConfig) // both modes available
    sessionStore.updateSessionConfig({ mode: 'realtime' }) // currently realtime
    sessionStore.setTurnStatus('ready') // toggle enabled (no turn in flight)
    render(<ModeToggle />)

    fireEvent.click(screen.getByRole('button', { name: 'Cascade' }))

    // the toggle hands the manager (prev, next) so it can tear down on a switch-AWAY from realtime
    expect(realtimeConnectionManager.onModeSwitch).toHaveBeenCalledWith('realtime', 'cascade')
    expect(sessionStore.getState().mode).toBe('cascade') // the store mode still flips (additive to teardown)
  })
})

describe('ModeToggle — backend mode-switch on an active session (Finding 2c, brief 050)', () => {
  it('POSTs setMode and resyncs the mode from the response when switching mid-session', async () => {
    sessionStore.reset()
    sessionStore.loadConfig(fullConfig)
    sessionStore.sessionStarted(session('cascade')) // active session, currently cascade
    vi.mocked(sessionsApi.setMode).mockResolvedValue(session('realtime'))

    render(<ModeToggle />)
    fireEvent.click(screen.getByRole('button', { name: 'Realtime' }))

    // the toggle dispatches the DI'd switchMode flow → POST /mode (2c: keep the backend CurrentMode in sync)
    await waitFor(() => expect(sessionsApi.setMode).toHaveBeenCalledWith('session_abc', 'realtime'))
    // the store mode is resynced from the authoritative returned session (Q4)
    await waitFor(() => expect(sessionStore.getState().mode).toBe('realtime'))
  })
})

describe('ModeToggle — clear-before-retry self-recovery (G.4/054 Fix B)', () => {
  it('clears prior errors before dispatching the switch so a failed switch never strands the UI', async () => {
    sessionStore.reset()
    sessionStore.loadConfig(fullConfig)
    sessionStore.sessionStarted(session('cascade')) // active, cascade
    // a lingering error from a previous failed switch (the "must refresh to recover" symptom)
    sessionStore.addError({ code: 'session.mode_switch_failed', safeMessage: 'x', retryable: true })
    expect(sessionStore.getState().errors).toHaveLength(1)
    vi.mocked(sessionsApi.setMode).mockResolvedValue(session('realtime'))

    render(<ModeToggle />)
    fireEvent.click(screen.getByRole('button', { name: 'Realtime' }))

    // cleared SYNCHRONOUSLY at dispatch — switchMode self-clears at the start of a real switch (before its
    // first await), so the toggle-click path self-recovers and the lingering banner can't strand the UI.
    expect(sessionStore.getState().errors).toEqual([])
    // and the switch still proceeds to the backend afterwards
    await waitFor(() => expect(sessionsApi.setMode).toHaveBeenCalledWith('session_abc', 'realtime'))
    await waitFor(() => expect(sessionStore.getState().mode).toBe('realtime'))
  })
})
