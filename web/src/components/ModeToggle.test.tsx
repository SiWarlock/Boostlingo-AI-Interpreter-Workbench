// @vitest-environment jsdom
import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import ModeToggle from './ModeToggle'
import { sessionStore } from '../state/sessionStore'
import type { ConfigResponse, TurnStatus } from '../types/domain'

// PRD/ARCH-020 transition #1, held to a bar at the render level: the mode toggle is disabled while a
// turn is in flight (recording/processing/playing) and enabled otherwise. Exercises D.2's ModeToggle +
// canToggleMode end-to-end through React. Per-file jsdom env (Q1) so the node-env unit suite is untouched.

const fullConfig: ConfigResponse = {
  realtime: { configured: true, models: ['gpt-realtime', 'gpt-realtime-mini'] },
  cascade: {
    stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
    translation: { configured: true, provider: 'openai', models: ['gpt-5.4-nano', 'gpt-5.4-mini'] },
    tts: { configured: true, provider: 'openai', model: 'gpt-4o-mini-tts' },
  },
  languages: ['en', 'es'],
  pricingConfigVersion: 'v',
}

afterEach(() => {
  cleanup()
  sessionStore.reset()
})

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
