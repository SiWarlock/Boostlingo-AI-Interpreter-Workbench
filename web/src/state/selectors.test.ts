import { describe, expect, it } from 'vitest'
import {
  availableModels,
  canStartRecording,
  canStopRecording,
  canToggleMode,
  modeAvailability,
} from './selectors'
import type { ConfigResponse, SessionStatus, TurnStatus } from '../types/domain'

function config(overrides: Partial<ConfigResponse> = {}): ConfigResponse {
  return {
    realtime: { configured: true, models: ['gpt-realtime', 'gpt-realtime-mini'] },
    cascade: {
      stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
      translation: {
        configured: true,
        provider: 'openai',
        models: ['gpt-5.4-nano', 'gpt-5.4-mini'],
      },
      tts: { configured: true, provider: 'openai', model: 'gpt-4o-mini-tts' },
    },
    languages: ['en', 'es'],
    pricingConfigVersion: '2026-05-28-payg-estimates',
    ...overrides,
  }
}

describe('modeAvailability', () => {
  it('both modes available when realtime + all three cascade stages are configured', () => {
    expect(modeAvailability(config())).toEqual({ realtime: true, cascade: true })
  })

  it('cascade unavailable when any one stage is unconfigured (full pipeline required)', () => {
    const oneStageDown = config({
      cascade: {
        stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
        translation: { configured: false, provider: 'openai', models: [] },
        tts: { configured: true, provider: 'openai', model: 'gpt-4o-mini-tts' },
      },
    })
    expect(modeAvailability(oneStageDown)).toEqual({ realtime: true, cascade: false })
  })

  it('both unavailable for undefined config (not-yet-loaded → nothing enabled)', () => {
    expect(modeAvailability(undefined)).toEqual({ realtime: false, cascade: false })
  })
})

describe('availableModels', () => {
  it('returns the catalog model lists for configured capabilities', () => {
    expect(availableModels(config())).toEqual({
      realtimeModels: ['gpt-realtime', 'gpt-realtime-mini'],
      translationModels: ['gpt-5.4-nano', 'gpt-5.4-mini'],
    })
  })

  it('undefined config → empty lists', () => {
    expect(availableModels(undefined)).toEqual({ realtimeModels: [], translationModels: [] })
  })

  it('reads the catalog regardless of the configured flag (configured gates the mode, not the models)', () => {
    // Backend ConfigService always populates the model catalogs; `configured` is key-presence only
    // and gates mode enablement (modeAvailability), NOT the selectable model list. Gating the list on
    // `configured` would empty the selector when a key is absent and break session-create (both models
    // are [Required] on CreateSessionRequest).
    const realtimeKeyAbsent = config({
      realtime: { configured: false, models: ['gpt-realtime', 'gpt-realtime-mini'] },
    })
    expect(availableModels(realtimeKeyAbsent).realtimeModels).toEqual([
      'gpt-realtime',
      'gpt-realtime-mini',
    ])
    expect(availableModels(realtimeKeyAbsent).translationModels).toEqual([
      'gpt-5.4-nano',
      'gpt-5.4-mini',
    ])
  })
})

describe('canToggleMode', () => {
  it('blocks the toggle during an active turn (recording/processing/playing)', () => {
    expect(canToggleMode('recording')).toBe(false)
    expect(canToggleMode('processing')).toBe(false)
    expect(canToggleMode('playing')).toBe(false)
  })

  it('allows the toggle when no turn is in flight', () => {
    expect(canToggleMode('ready')).toBe(true)
    expect(canToggleMode('captured')).toBe(true)
    expect(canToggleMode('completed')).toBe(true)
    expect(canToggleMode('failed')).toBe(true)
  })
})

describe('recording transitions (ARCH-007 table)', () => {
  const at = (sessionStatus: SessionStatus, turnStatus: TurnStatus) => ({
    sessionStatus,
    turnStatus,
  })

  it('canStartRecording: active/readyForTurn session AND a ready/completed/failed turn', () => {
    expect(canStartRecording(at('active', 'ready'))).toBe(true)
    expect(canStartRecording(at('readyForTurn', 'ready'))).toBe(true)
    expect(canStartRecording(at('active', 'completed'))).toBe(true) // start the next turn
    expect(canStartRecording(at('active', 'failed'))).toBe(true) // retry after a failed turn
    // blocked mid-turn
    expect(canStartRecording(at('active', 'recording'))).toBe(false)
    expect(canStartRecording(at('active', 'processing'))).toBe(false)
    expect(canStartRecording(at('active', 'playing'))).toBe(false)
    // blocked when the session isn't started
    expect(canStartRecording(at('idle', 'ready'))).toBe(false)
    expect(canStartRecording(at('configured', 'ready'))).toBe(false)
    expect(canStartRecording(at('ended', 'ready'))).toBe(false)
  })

  it('canStopRecording: only while recording', () => {
    expect(canStopRecording(at('active', 'recording'))).toBe(true)
    expect(canStopRecording(at('active', 'ready'))).toBe(false)
    expect(canStopRecording(at('active', 'processing'))).toBe(false)
    expect(canStopRecording(at('active', 'playing'))).toBe(false)
  })
})
