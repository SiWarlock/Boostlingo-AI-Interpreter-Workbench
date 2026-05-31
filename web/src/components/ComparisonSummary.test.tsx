// @vitest-environment jsdom
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

// Mock the DI'd load flow — the component is a thin render-from-data shell over it (ARCH-007). The flow
// itself (getSummary + getSession + aggregate) is unit-tested in comparisonActions.test.ts.
vi.mock('../state/comparisonActions', () => ({ loadComparison: vi.fn() }))

import ComparisonSummary from './ComparisonSummary'
import { loadComparison } from '../state/comparisonActions'
import { sessionStore } from '../state/sessionStore'
import type { ComparisonData } from '../state/comparisonActions'
import type { InterpretationSession, SessionSummary } from '../types/domain'

function activeSession(): InterpretationSession {
  return {
    sessionId: 'session_abc',
    startedAt: '2026-05-30T00:00:00+00:00',
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

function summary(): SessionSummary {
  return {
    turnCount: 2,
    realtime: {
      turnCount: 1,
      avgSpeechEndToFirstAudioMs: 900,
      avgSpeechEndToPlaybackMs: 1100,
      estimatedCostPerMinuteUsd: 0.5,
      errorCount: 0,
      avgSttFinalMs: null,
      avgTranslationFinalMs: null,
      avgTtsFirstAudioMs: null,
    },
    cascade: {
      turnCount: 1,
      avgSpeechEndToFirstAudioMs: null, // the no-client-latency-channel case → renders n/a
      avgSpeechEndToPlaybackMs: null,
      estimatedCostPerMinuteUsd: 0.3,
      errorCount: 0,
      avgSttFinalMs: 120,
      avgTranslationFinalMs: 240,
      avgTtsFirstAudioMs: 360,
    },
    wer: { sampleCount: 1, avgWer: 0.25 },
    computedAt: '2026-05-30T00:00:00+00:00',
    pricingConfigVersion: 'v',
  }
}

afterEach(() => {
  cleanup()
  sessionStore.reset()
  vi.clearAllMocks()
})

describe('ComparisonSummary', () => {
  it('renders the by-mode comparison (latency / cost / errors / turns) plus WER and total', async () => {
    vi.mocked(loadComparison).mockResolvedValue({
      summary: summary(),
      byVariant: [],
      models: null,
    } as ComparisonData)
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    const realtime = await screen.findByLabelText('Realtime-summary')
    const cascade = screen.getByLabelText('Cascade-summary')
    expect(within(realtime).getByText(/Estimated \$0\.50\/min/)).toBeInTheDocument() // per-mode cost/min
    expect(within(realtime).getByText(/Speech→first audio:\s*900 ms/i)).toBeInTheDocument()
    expect(within(cascade).getByText(/Turns:\s*1/i)).toBeInTheDocument()
    expect(screen.getByText(/Total turns:\s*2/i)).toBeInTheDocument()
    expect(screen.getByText(/Avg WER:\s*25\.0%/)).toBeInTheDocument()
  })

  it('attributes the model per mode on the cards (from providerProfile — cost-independent, 056 bug 6)', async () => {
    vi.mocked(loadComparison).mockResolvedValue({
      summary: summary(),
      byVariant: [],
      models: { cascade: 'gpt-5-nano', realtime: 'gpt-realtime' },
    } as ComparisonData)
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    const realtime = await screen.findByLabelText('Realtime-summary')
    const cascade = screen.getByLabelText('Cascade-summary')
    // model identity surfaced on the mode cards (independent of the cost-by-variant table below)
    expect(within(realtime).getByText(/Model:\s*gpt-realtime/i)).toBeInTheDocument()
    expect(within(cascade).getByText(/Model:\s*gpt-5-nano/i)).toBeInTheDocument()
  })

  it('renders "Model: n/a" on the cards when models is null (getSession degraded — 056 bug 6)', async () => {
    vi.mocked(loadComparison).mockResolvedValue({
      summary: summary(),
      byVariant: null,
      models: null,
    } as ComparisonData)
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    const cascade = await screen.findByLabelText('Cascade-summary')
    // the model line degrades to n/a (never blank/crash) when the session source failed
    expect(within(cascade).getByText(/Model:\s*n\/a/i)).toBeInTheDocument()
  })

  it('renders n/a (never 0) for a missing ModeSummary latency field', async () => {
    vi.mocked(loadComparison).mockResolvedValue({
      summary: summary(),
      byVariant: [],
      models: null,
    } as ComparisonData)
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    const cascade = await screen.findByLabelText('Cascade-summary')
    // cascade.avgSpeechEndToFirstAudioMs is null → "n/a", never "0 ms".
    expect(within(cascade).getByText(/Speech→first audio:\s*n\/a/i)).toBeInTheDocument()
    expect(screen.queryByText(/Speech→first audio:\s*0 ms/i)).toBeNull()
  })

  it('renders the by-model-variant cost breakdown rows', async () => {
    const byVariant: ComparisonData['byVariant'] = [
      { mode: 'cascade', model: 'gpt-5-nano', avgCostPerMinuteUsd: 0.3, turnCount: 2 },
      { mode: 'realtime', model: 'gpt-realtime', avgCostPerMinuteUsd: 0.5, turnCount: 1 },
    ]
    vi.mocked(loadComparison).mockResolvedValue({ summary: summary(), byVariant, models: null })
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    const variants = await screen.findByLabelText('cost-by-variant')
    expect(within(variants).getByText(/gpt-5-nano/)).toBeInTheDocument()
    expect(within(variants).getByText(/Estimated \$0\.30\/min/)).toBeInTheDocument()
    expect(within(variants).getByText(/gpt-realtime/)).toBeInTheDocument()
  })

  it('renders "Per-variant cost unavailable." when the per-variant source failed (byVariant null)', async () => {
    vi.mocked(loadComparison).mockResolvedValue({
      summary: summary(),
      byVariant: null,
      models: null,
    })
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    expect(await screen.findByText(/Per-variant cost unavailable\./i)).toBeInTheDocument()
  })

  it('renders "No priced turns yet." when the session ran but no turns were priced (byVariant [])', async () => {
    vi.mocked(loadComparison).mockResolvedValue({
      summary: summary(),
      byVariant: [],
      models: null,
    } as ComparisonData)
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    expect(await screen.findByText(/No priced turns yet\./i)).toBeInTheDocument()
  })

  it('shows a "run some turns to compare" state when there is no session/data', async () => {
    // No session seeded → sessionId null → no data → the empty state.
    render(<ComparisonSummary />)

    expect(await screen.findByText(/run some turns to compare/i)).toBeInTheDocument()
    expect(loadComparison).not.toHaveBeenCalled()
  })

  it('shows the empty state when data loads but the summary has zero turns', async () => {
    vi.mocked(loadComparison).mockResolvedValue({
      summary: { ...summary(), turnCount: 0 },
      byVariant: [],
      models: null,
    } as ComparisonData)
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    await screen.findByText(/run some turns to compare/i)
    await waitFor(() => expect(loadComparison).toHaveBeenCalledTimes(1))
    // The turnCount-0 gate fires even with a non-null data result — no by-mode view renders.
    expect(screen.queryByLabelText('Realtime-summary')).toBeNull()
  })

  it('renders "No turns in this mode." for a mode the session never ran', async () => {
    vi.mocked(loadComparison).mockResolvedValue({
      summary: { ...summary(), realtime: null },
      byVariant: [],
      models: null,
    } as ComparisonData)
    sessionStore.sessionStarted(activeSession())

    render(<ComparisonSummary />)

    const realtime = await screen.findByLabelText('Realtime-summary')
    expect(within(realtime).getByText(/No turns in this mode\./i)).toBeInTheDocument()
  })
})
