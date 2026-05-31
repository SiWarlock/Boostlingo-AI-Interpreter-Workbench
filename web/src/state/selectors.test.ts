import { describe, expect, it } from 'vitest'
import {
  availableModels,
  canStartRecording,
  canStopRecording,
  canToggleMode,
  deriveTurnMetrics,
  formatCostPerMinute,
  modeAvailability,
} from './selectors'
import type {
  ConfigResponse,
  CostEstimate,
  LatencyEvent,
  SessionStatus,
  TurnStatus,
  TurnViewModel,
} from '../types/domain'

function config(overrides: Partial<ConfigResponse> = {}): ConfigResponse {
  return {
    realtime: { configured: true, models: ['gpt-realtime', 'gpt-realtime-mini'] },
    cascade: {
      stt: { configured: true, provider: 'deepgram', model: 'nova-3' },
      translation: {
        configured: true,
        provider: 'openai',
        models: ['gpt-5-nano', 'gpt-5-mini'],
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
      translationModels: ['gpt-5-nano', 'gpt-5-mini'],
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
      'gpt-5-nano',
      'gpt-5-mini',
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

// --- D.6 metrics derivation -------------------------------------------------------------------

function latencyEvent(
  name: string,
  timestamp: string,
  overrides: Partial<LatencyEvent> = {},
): LatencyEvent {
  return {
    name,
    stage: 'overall',
    timestamp,
    relativeMs: 0,
    clockSource: 'browser',
    metadata: {},
    ...overrides,
  }
}

function turn(overrides: Partial<TurnViewModel> = {}): TurnViewModel {
  return {
    turnId: 'turn_001',
    mode: 'cascade',
    direction: { source: 'en', target: 'es' },
    status: 'completed',
    startedAt: '2026-05-29T00:00:00.000Z',
    sourceTranscript: [],
    targetTranscript: [],
    latency: {},
    errors: [],
    ...overrides,
  }
}

describe('deriveTurnMetrics', () => {
  it('computes top-level deltas via absolute-timestamp Between (never relativeMs)', () => {
    // relativeMs is left at the placeholder 0 on every event — proving the math uses absolute
    // timestamps, not relativeMs (lesson §7 / the load-bearing metrics-sourcing design).
    const t = turn({
      latencyEvents: [
        latencyEvent('turn.recording.started', '2026-05-29T00:00:00.000Z'),
        latencyEvent('turn.recording.stopped', '2026-05-29T00:00:02.000Z'), // speechEnd @ +2000ms
        latencyEvent('tts.first_audio', '2026-05-29T00:00:02.600Z', {
          stage: 'tts',
          clockSource: 'server',
        }), // +600ms from speechEnd
        latencyEvent('playback.started', '2026-05-29T00:00:02.900Z'), // +900ms from speechEnd
        latencyEvent('tts.complete', '2026-05-29T00:00:03.500Z', {
          stage: 'tts',
          clockSource: 'server',
        }), // +3500ms from start
      ],
    })

    const m = deriveTurnMetrics(t)

    expect(m.speechEndToFirstAudioMs).toBe(600)
    expect(m.speechEndToPlaybackMs).toBe(900)
    expect(m.totalTurnMs).toBe(3500)
  })

  it('a metric whose endpoint event is absent → undefined (renders n/a, never 0)', () => {
    const t = turn({
      latencyEvents: [
        latencyEvent('turn.recording.started', '2026-05-29T00:00:00.000Z'),
        latencyEvent('turn.recording.stopped', '2026-05-29T00:00:02.000Z'),
        // no tts.first_audio, no playback.started, no terminal event
      ],
    })

    const m = deriveTurnMetrics(t)

    expect(m.speechEndToFirstAudioMs).toBeUndefined()
    expect(m.speechEndToPlaybackMs).toBeUndefined()
    expect(m.totalTurnMs).toBeUndefined()
  })

  it('does NOT clamp a cross-clock negative delta (ARCH-013 discloses skew, never hides it)', () => {
    const t = turn({
      latencyEvents: [
        latencyEvent('turn.recording.stopped', '2026-05-29T00:00:02.000Z'), // browser clock
        // server clock slightly behind → first_audio timestamp precedes the browser speechEnd
        latencyEvent('tts.first_audio', '2026-05-29T00:00:01.950Z', {
          stage: 'tts',
          clockSource: 'server',
        }),
      ],
    })

    expect(deriveTurnMetrics(t).speechEndToFirstAudioMs).toBe(-50) // NOT clamped to 0
  })

  // A1 (brief 049): ARCH-013 documents speech_end_to_first_audio_ms as
  // `tts.first_audio ?? realtime.first_audio_delta ?? playback.started`. The realtime fallback was
  // missing (only tts.first_audio read), so realtime turns showed a permanent n/a headline.
  it('realtime: uses realtime.first_audio_delta when tts.first_audio is absent (ARCH-013 chain)', () => {
    const t = turn({
      mode: 'realtime',
      latencyEvents: [
        latencyEvent('turn.recording.started', '2026-05-29T00:00:00.000Z'),
        latencyEvent('turn.recording.stopped', '2026-05-29T00:00:02.000Z'), // speechEnd @ +2000ms
        // realtime emits NO tts.first_audio; the per-turn sink stamps realtime.first_audio_delta (post-stop)
        latencyEvent('realtime.first_audio_delta', '2026-05-29T00:00:02.800Z', {
          stage: 'realtime',
        }), // +800ms
      ],
    })
    expect(deriveTurnMetrics(t).speechEndToFirstAudioMs).toBe(800)
  })

  it('prefers tts.first_audio over realtime.first_audio_delta when both are present (cascade-first chain)', () => {
    const t = turn({
      latencyEvents: [
        latencyEvent('turn.recording.stopped', '2026-05-29T00:00:02.000Z'),
        latencyEvent('tts.first_audio', '2026-05-29T00:00:02.600Z', {
          stage: 'tts',
          clockSource: 'server',
        }), // +600
        latencyEvent('realtime.first_audio_delta', '2026-05-29T00:00:02.900Z', {
          stage: 'realtime',
        }), // +900
      ],
    })
    expect(deriveTurnMetrics(t).speechEndToFirstAudioMs).toBe(600) // tts.first_audio wins
  })

  it('falls back to playback.started when no first-audio marker exists (ARCH-013 chain tail)', () => {
    const t = turn({
      latencyEvents: [
        latencyEvent('turn.recording.stopped', '2026-05-29T00:00:02.000Z'),
        latencyEvent('playback.started', '2026-05-29T00:00:03.000Z'), // +1000
      ],
    })
    expect(deriveTurnMetrics(t).speechEndToFirstAudioMs).toBe(1000)
  })

  it('prefers turn.completed over tts.complete as the totalTurn endpoint (backend-canonical terminal)', () => {
    const t = turn({
      latencyEvents: [
        latencyEvent('turn.recording.started', '2026-05-29T00:00:00.000Z'),
        latencyEvent('tts.complete', '2026-05-29T00:00:03.500Z', {
          stage: 'tts',
          clockSource: 'server',
        }),
        latencyEvent('turn.completed', '2026-05-29T00:00:03.800Z', { clockSource: 'server' }),
      ],
    })

    expect(deriveTurnMetrics(t).totalTurnMs).toBe(3800)
  })

  it('derives per-stage DURATIONS from the marker timeline (ARCH-013); ignores the raw latency.stages map', () => {
    // bug 1 (056): the per-stage display was n/a because `stages` was the raw {eventName: relativeMs}
    // passthrough (keys never matched STAGE_META's stt/translation/tts). ARCH-013 cascade stage durations:
    // stt.final−cascade.audio.received · translation.final−translation.started · tts.complete−tts.started,
    // computed via absolute-timestamp Between (lesson §7 — NEVER relativeMs). The fixture timeline.
    const base = Date.parse('2026-05-31T00:00:00.000Z')
    const at = (ms: number) => new Date(base + ms).toISOString()
    const t = turn({
      // a stale relativeMs passthrough map that must NO LONGER be the source (the bug-1 root cause)
      latency: { stages: { 'stt.final': 99999 } },
      latencyEvents: [
        latencyEvent('cascade.audio.received', at(0)),
        latencyEvent('stt.final', at(4010), { stage: 'stt', clockSource: 'server' }),
        latencyEvent('translation.started', at(4460), {
          stage: 'translation',
          clockSource: 'server',
        }),
        latencyEvent('translation.final', at(4915), {
          stage: 'translation',
          clockSource: 'server',
        }),
        latencyEvent('tts.started', at(6034), { stage: 'tts', clockSource: 'server' }),
        latencyEvent('tts.complete', at(6074), { stage: 'tts', clockSource: 'server' }),
      ],
    })

    // STT = 4010−0 = 4010 from THESE exact constructed timestamps (the brief cited 4009 from the live
    // turn's absolute-timestamp rounding — same anchors, ±1ms). Translation 455, TTS 40 match the brief.
    expect(deriveTurnMetrics(t).stages).toEqual({ stt: 4010, translation: 455, tts: 40 })
  })

  it('omits a stage whose marker is absent (honest n/a — never a fabricated 0)', () => {
    const base = Date.parse('2026-05-31T00:00:00.000Z')
    const at = (ms: number) => new Date(base + ms).toISOString()
    const t = turn({
      latencyEvents: [
        latencyEvent('cascade.audio.received', at(0)),
        latencyEvent('stt.final', at(4010), { stage: 'stt' }),
        // no translation.* and no tts.* markers
      ],
    })

    const s = deriveTurnMetrics(t).stages ?? {}
    expect(s.stt).toBe(4010)
    expect(s.translation).toBeUndefined()
    expect(s.tts).toBeUndefined()
  })

  it('omits a NEGATIVE stage duration (same-clock mis-stamp → honest n/a, never a poisoned stage bar)', () => {
    // Stage markers are all SERVER clock, so a negative duration is a backend mis-stamp (NOT disclosed
    // cross-clock skew). It must not flow into the panel's stage-bar divisor — omit it (honest n/a).
    const base = Date.parse('2026-05-31T00:00:00.000Z')
    const at = (ms: number) => new Date(base + ms).toISOString()
    const t = turn({
      latencyEvents: [
        latencyEvent('cascade.audio.received', at(0)),
        latencyEvent('stt.final', at(4010), { stage: 'stt' }),
        // tts.complete BEFORE tts.started → a negative (mis-stamped) TTS duration
        latencyEvent('tts.started', at(6074), { stage: 'tts' }),
        latencyEvent('tts.complete', at(6034), { stage: 'tts' }),
      ],
    })

    const s = deriveTurnMetrics(t).stages ?? {}
    expect(s.stt).toBe(4010)
    expect(s.tts).toBeUndefined() // negative omitted — NOT a negative, NOT a fabricated 0
  })

  it('cascade: anchors speech-end on stt.final (endpointing ≈ true speech-end), not the manual recording.stopped', () => {
    // bug 3 (056): pre-VAD, turn.recording.stopped is a MANUAL stop held seconds after speech → a
    // negative, misleading responsiveness (tts.first_audio precedes it). stt.final (Deepgram endpointing)
    // is the hold-robust speech-end proxy. Falls back to recording.stopped when stt.final is absent.
    const base = Date.parse('2026-05-31T00:00:00.000Z')
    const at = (ms: number) => new Date(base + ms).toISOString()
    const t = turn({
      latencyEvents: [
        latencyEvent('stt.final', at(4010), { stage: 'stt', clockSource: 'server' }),
        latencyEvent('tts.first_audio', at(6034), { stage: 'tts', clockSource: 'server' }),
        latencyEvent('turn.recording.stopped', at(9796)), // manual stop — LATER than first audio
      ],
    })

    // responsiveness = tts.first_audio − stt.final = 2024 (positive), NOT − recording.stopped (negative)
    expect(deriveTurnMetrics(t).speechEndToFirstAudioMs).toBe(2024)
  })
})

describe('formatCostPerMinute', () => {
  function cost(overrides: Partial<CostEstimate> = {}): CostEstimate {
    return {
      provider: 'cascade',
      model: 'gpt-5-nano',
      pricingBasis: 'composite',
      estimatedUsd: 0.0021,
      estimatedUsdPerMinute: 0.42,
      units: {},
      pricingConfigVersion: '2026-05-28-payg-estimates',
      assumptions: ['TTS cost uses a character-count proxy'],
      ...overrides,
    }
  }

  it('formats an available per-minute estimate as a qualified "Estimated $X.XX/min" string', () => {
    expect(formatCostPerMinute(cost())).toBe('Estimated $0.42/min')
    expect(formatCostPerMinute(cost({ estimatedUsdPerMinute: 1 }))).toBe('Estimated $1.00/min')
  })

  it('returns n/a when the per-minute estimate is unavailable (null value or absent estimate)', () => {
    expect(formatCostPerMinute(cost({ estimatedUsdPerMinute: null }))).toBe('n/a')
    expect(formatCostPerMinute(undefined)).toBe('n/a')
    expect(formatCostPerMinute(null)).toBe('n/a')
  })
})
