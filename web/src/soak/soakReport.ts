import type { InterpretationMode } from '../types/domain'
import type { DriftVerdict, Overlap } from './soakDrift'
import type { LeakVerdict } from './soakLeak'
import type { SoakWerSummary } from './soakWer'

// The structured SoakReport is the deliverable artifact for one mode's 5-min run (fed to the ARCH-020
// checklist + the G.5 write-up by 088/089). Its three ARCH-020 booleans are DERIVED from the computed
// verdicts + disconnect count — never hand-set — so the §15 three-check gate can't drift from the
// underlying measurements. Pure assembly.

export type Arch020Checks = {
  noDisconnect: boolean
  noDriftOverlap: boolean
  noLeak: boolean
}

export type SoakReportInputs = {
  mode: InterpretationMode
  durationSec: number
  turnCount: number
  disconnectCount: number
  latency: DriftVerdict
  overlaps: Overlap[]
  skewSlope: number
  heapLeak: LeakVerdict
  werSummary: SoakWerSummary
}

export type SoakReport = {
  mode: InterpretationMode
  durationSec: number
  turnCount: number
  disconnectCount: number
  latency: DriftVerdict
  overlaps: Overlap[]
  skewSlope: number
  heapLeak: LeakVerdict
  werSummary: SoakWerSummary
  arch020: Arch020Checks
}

export function assembleSoakReport(inputs: SoakReportInputs): SoakReport {
  const arch020: Arch020Checks = {
    noDisconnect: inputs.disconnectCount === 0,
    // Drift = latency slope PASS (no accumulating lag) AND no unplanned overlaps.
    noDriftOverlap: inputs.latency.pass && inputs.overlaps.length === 0,
    noLeak: inputs.heapLeak.pass,
  }
  return { ...inputs, arch020 }
}
