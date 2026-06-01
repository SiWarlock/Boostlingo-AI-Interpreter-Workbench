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

// How the overlap output-duration was derived for this (per-mode) run: realtime is 'token-derived' (precise,
// reported response.done.usage) while cascade is 'char-estimate' (the rougher disclosed §36 char→minutes cost
// basis); 'none' when no duration signal existed (overlap unmeasured). Discloses that a cascade
// overlapMeasured:true is an ESTIMATE-based check, not an exact measurement (093). (093)
export type OverlapBasis = 'token-derived' | 'char-estimate' | 'none'

export type SoakReportInputs = {
  mode: InterpretationMode
  durationSec: number
  turnCount: number
  disconnectCount: number
  latency: DriftVerdict
  overlaps: Overlap[]
  // Whether overlap was actually MEASURED (any turn carried a playback-end stamp). With no per-turn
  // output-audio duration every playbackEndMs is null and detectOverlaps returns [] — so this discloses
  // "unmeasured" rather than letting noDriftOverlap read as a silent "checked, none found" (honest-degrade).
  overlapMeasured: boolean
  overlapBasis: OverlapBasis
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
  overlapMeasured: boolean
  overlapBasis: OverlapBasis
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
