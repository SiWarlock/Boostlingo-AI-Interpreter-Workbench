import { useState } from 'react'
import type { InterpretationMode } from '../types/domain'
import type { SoakReport } from './soakReport'
import { runSoakHarness } from './composeSoakDrive'

// Dev-only `?soak=1` entry (089b). Mounted ONLY under `import.meta.env.DEV` + `?soak=1` (main.tsx) — never
// in the normal demo UI (ARCH-007 clean-separation preserved; the harness injects at the capture boundary,
// it does not bypass the store). Picks a mode, runs the 5-min synthetic soak, and renders the SoakReport.
// SMOKE (no unit test) — the deterministic engine/seams underneath are TDD'd (087/088/089a/089b).
export default function SoakPanel() {
  const [mode, setMode] = useState<InterpretationMode>('cascade')
  const [running, setRunning] = useState(false)
  const [report, setReport] = useState<SoakReport | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function run() {
    setRunning(true)
    setError(null)
    setReport(null)
    try {
      setReport(await runSoakHarness(mode))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Soak run failed.')
    } finally {
      setRunning(false)
    }
  }

  return (
    <main style={{ padding: 24, fontFamily: 'system-ui, sans-serif', maxWidth: 720 }}>
      <h1>G.4 Soak Harness (dev)</h1>
      <p>
        Drives a scripted 5-minute bidirectional EN↔ES conversation through the real pipeline with
        synthesized audio, then reports ARCH-020 stability + latency/cost/WER.
      </p>
      <fieldset disabled={running} style={{ marginBottom: 16 }}>
        <label>
          Mode:{' '}
          <select value={mode} onChange={(e) => setMode(e.target.value as InterpretationMode)}>
            <option value="cascade">cascade</option>
            <option value="realtime">realtime</option>
          </select>
        </label>{' '}
        <button type="button" onClick={run} disabled={running}>
          {running ? 'Running…' : 'Run soak'}
        </button>
      </fieldset>

      {error !== null && <p role="alert">Error: {error}</p>}

      {report !== null && (
        <section>
          <h2>SoakReport — {report.mode}</h2>
          <ul>
            <li>no disconnect: {String(report.arch020.noDisconnect)}</li>
            <li>no drift/overlap: {String(report.arch020.noDriftOverlap)}</li>
            <li>no leak: {String(report.arch020.noLeak)}</li>
          </ul>
          {!report.overlapMeasured && (
            <p>
              <strong>overlap: NOT MEASURED</strong> — no per-turn output-audio duration; drift via
              latency-slope only.
            </p>
          )}
          {report.overlapMeasured && (
            <p>
              overlap basis: <strong>{report.overlapBasis}</strong>
              {report.overlapBasis === 'char-estimate' &&
                ' (cascade char→minutes estimate — rougher than realtime reported tokens)'}
            </p>
          )}
          <ul>
            <li>turns: {report.turnCount}</li>
            <li>duration: {report.durationSec}s</li>
            <li>disconnects: {report.disconnectCount}</li>
            <li>
              latency slope: {report.latency.slopeMsPerTurn.toFixed(2)} ms/turn (pass:{' '}
              {String(report.latency.pass)})
            </li>
            <li>overlaps: {report.overlaps.length}</li>
            <li>playback-skew slope: {report.skewSlope}</li>
            <li>
              heap-leak slope: {report.heapLeak.slopeBytesPerSample.toFixed(0)} B/sample (pass:{' '}
              {String(report.heapLeak.pass)})
            </li>
            <li>
              WER: mean {report.werSummary.meanWer ?? 'n/a'} / median{' '}
              {report.werSummary.medianWer ?? 'n/a'} (n={report.werSummary.count})
            </li>
          </ul>
          <pre>{JSON.stringify(report, null, 2)}</pre>
        </section>
      )}
    </main>
  )
}
