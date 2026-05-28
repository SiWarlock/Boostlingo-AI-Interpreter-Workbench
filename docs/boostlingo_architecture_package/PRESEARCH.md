# PRESEARCH.md — AI Interpreter Workbench

> **Status:** Planning artifact for the AI Interpreter Workbench MVP.
>
> **Source inputs:** Boostlingo PRD, Deep Agentic Architecture Planning Playbook, and planning decisions confirmed in chat.
>
> **Purpose:** Capture product understanding, users, workflows, domain model, requirements, assumptions, risks, and MVP-scoped inferences before architecture drafting.

---

## Phase 0 — PRD Intake

### Product in One Sentence

AI Interpreter Workbench is a browser-based architecture evaluation tool that implements and instruments two live interpretation approaches: OpenAI Realtime voice-to-voice and a composable STT → Translation → TTS cascade pipeline.

### What the Product Is

The product is an engineering workbench for comparing architecture patterns for live AI interpretation. It lets a local evaluator speak into a browser, select English → Spanish or Spanish → English, run either Realtime or Cascade mode, see transcripts/audio/latency/cost/quality signals, persist session results, and produce a defensible comparison write-up.

The project is evaluated across three surfaces:

1. A live demo of both modes.
2. Code quality and architecture boundaries.
3. A reasoned comparison write-up supported by actual measured metrics.

### What the Product Is Not

This is not a production Boostlingo replacement. It is not a multi-user call platform, human interpreter marketplace, account system, billing system, SIP/telephony product, compliance workflow, or full language-pair platform.

It is also not trying to solve full semantic translation evaluation. WER is included only as a scripted STT transcript quality metric.

### Primary Problem

Boostlingo needs to understand when to prefer direct voice-to-voice models versus composable cascade pipelines for live interpretation. The tradeoffs around latency, cost, quality, vendor lock-in, language-pair onboarding, and operational control are not obvious without building and instrumenting both.

### Primary User

The primary user is a technical evaluator/candidate/reviewer running the workbench locally to compare both interpretation architectures.

### Core Workflow

1. User opens browser SPA.
2. User optionally enters a session/scenario label.
3. User selects explicit language direction: English → Spanish or Spanish → English.
4. User selects Realtime or Cascade mode.
5. User starts a local session.
6. User clicks Start Recording.
7. User speaks a phrase.
8. User clicks Stop Recording.
9. Selected mode processes the turn.
10. UI shows source transcript, target transcript, translated audio playback, latency metrics, cost estimate, errors, and quality/evaluation signals.
11. User repeats turns and may switch mode between turns.
12. Session results persist to local JSON files.
13. User reviews the comparison summary and uses it for the final recommendation.

### Explicit PRD Requirements

- Browser-based SPA with microphone capture.
- Browser audio playback.
- Realtime mode using OpenAI Realtime API with `gpt-realtime` or configured Realtime model.
- Cascade mode using STT → Translation → TTS.
- Streaming cascade pipeline.
- Mode switching UI.
- English ↔ Spanish language pair selection.
- Live source and target transcripts.
- Per-stage latency display visible to the user.
- Comparison write-up covering latency, quality, cost, controllability, and recommendation.
- Clean separation between mode-specific transport and mode-agnostic UI.
- Provider abstractions for STT, translation, and TTS.
- Targeted tests around cascade pipeline and provider boundaries.
- Error handling for provider failures, rate limits, timeouts, empty results, and mic permission denial.
- README with setup, run, and architecture overview.
- `AGENTS.md` or `CLAUDE.md` describing coding-agent usage.
- Meaningful git history.
- Realtime target: under 1.5s perceived latency, speech end → first audio out.
- Cascade target: under 3s, target under 2s with full streaming.
- Stability target: 5-minute back-and-forth conversation without disconnect, audio drift, or memory leaks.
- Build timebox: 3–4 days, roughly 15–20 hours.

### Implied Requirements

- Backend must own provider API keys.
- Browser must never contain standard OpenAI/Deepgram provider secrets.
- Realtime browser connection should use backend-minted ephemeral credentials.
- Shared session/turn model is required to compare modes.
- Shared latency event model is required to normalize metrics.
- Cascade needs event-shaped provider interfaces even when a provider returns a final-only response.
- File-based JSON persistence is enough; database is unnecessary.
- Raw audio persistence is deferred to avoid privacy/storage complexity.
- Cost/minute should be estimated from configurable pricing assumptions, not treated as billing-grade.
- WER should use scripted phrases with known references.
- Tests need fake providers so CI/local tests do not depend on paid APIs.
- Local-first setup must be polished enough to satisfy evaluation.

### External Dependencies

- OpenAI Realtime API.
- OpenAI text generation API for translation.
- OpenAI TTS API.
- Deepgram streaming STT API.
- Browser WebRTC and media APIs.
- Browser audio playback APIs.
- .NET backend runtime.
- TypeScript frontend tooling.
- Optional AWS deployment services after local stability.

### Ambiguities Resolved During Planning

| Area | Resolution |
|---|---|
| Main demo goal | Build both architectures, demo them, and explain tradeoffs. |
| Evaluation surface | Live app, code review, and comparison write-up. |
| Stack | TypeScript frontend + .NET/C# backend. |
| Deployment | Local-first, optional deployment after working. |
| Secrets | Provider keys stay server-side. |
| Mode switching | Stop/restart between turns is acceptable; no seamless mid-stream hot-swap. |
| UX model | Turn-based click start/stop. |
| Test persona | One local evaluator testing both directions. |
| Provider scope | One real provider per stage + interfaces/fakes. |
| Persistence | Local JSON files; no database; no saved raw audio. |
| WER | Scripted STT quality check, not full translation quality metric. |

### Initial Risk Areas

- Browser audio capture and playback complexity.
- Realtime WebRTC integration complexity.
- Cascade latency exceeding target.
- Provider API mismatch and streaming semantics mismatch.
- Bad or inconsistent latency definitions.
- Overbuilding provider flexibility.
- Cost estimates being mistaken for billing-grade numbers.
- WER being mistaken for full interpretation quality.
- Demo fragility due to multiple external APIs.
- Secrets exposure if provider keys leak to browser.

### Recommended Planning Mode

**Standard** planning mode.

Rationale: The timebox is short, but the project has real-time audio, external AI providers, instrumentation, measurable performance criteria, evaluator-facing architecture tradeoffs, and a build that Claude Code will implement from docs. Compact mode would be too thin; Expanded would be too heavy for the 15–20 hour build.

---

## Phase 1 — Product Mechanics

### Core Object of Value

The core object of value is an **instrumented interpretation session**.

A session is valuable because it captures evidence needed to compare Realtime and Cascade:

- Mode used per turn.
- Language direction.
- Source transcript.
- Target transcript.
- Translated audio playback result.
- Latency events.
- Cost estimate.
- Provider metadata.
- Errors and fallbacks.
- WER samples when applicable.

### State-Changing Actions

| Action | Actor | State Created/Changed |
|---|---|---|
| Start session | User | Creates active `InterpretationSession` |
| Set label | User | Adds optional scenario label |
| Select language direction | User | Sets `LanguageDirection` |
| Select mode | User | Sets `InterpretationMode` |
| Start recording | User | Creates active turn capture state |
| Stop recording | User | Finalizes input audio turn |
| Process Realtime turn | System | Produces Realtime result, events, metrics |
| Process Cascade turn | System | Produces STT, translation, TTS, metrics |
| Switch mode | User | Closes current transport and initializes next mode |
| Run WER phrase | User/System | Creates `WerResult` |
| End session | User/System | Finalizes summary and writes session file |

### Lifecycle

```text
Idle
→ Configured
→ Starting
→ Active
→ ReadyForTurn
→ Recording
→ Captured
→ Processing
→ Playing
→ TurnCompleted
→ ReadyForTurn
→ Ending
→ Ended
```

Failure overlays:

```text
MicPermissionDenied
ProviderUnavailable
ProviderTimeout
ProviderRateLimited
RealtimeDisconnected
EmptyTranscript
PlaybackFailed
PersistenceFailed
```

### Turn-Based UX

The MVP uses click-to-start and click-to-stop recording. This avoids always-listening, VAD tuning, barge-in, duplex interruption handling, and continuous audio drift complexity.

### Mode Switching

Mode switching is allowed only between turns. The app should prevent switching while recording, processing, or playing unless a future cancel action is added.

Switching sequence:

1. User changes mode toggle.
2. Current transport is stopped.
3. Provider connection/audio playback queues are reset.
4. Mode transition event is recorded.
5. Next turn uses the new mode.

### WER Mechanics

WER is Word Error Rate:

```text
WER = (Substitutions + Insertions + Deletions) / ReferenceWordCount
```

The MVP uses 8–12 scripted phrases with reference transcripts. The user reads a phrase, the app captures STT output, backend normalizes reference and hypothesis, computes WER, and stores the result.

WER is not a full interpretation-quality metric. It is a scoped STT transcript quality metric.

### Cost Mechanics

Cost is estimated live using configured pricing constants and provider usage metadata when available. It must be labeled as estimated.

---

## Phase 2 — Users, Actors, and Permissions

### Primary User

| Field | Description |
|---|---|
| Role | Technical evaluator / candidate / reviewer |
| Goal | Compare Realtime and Cascade architectures |
| Context | Needs to demo, inspect code, and defend tradeoffs |
| Success | Both modes work; metrics are visible; recommendation is defensible |
| Failure | App works as toy API demo but does not explain architectural fit |

### Secondary User

| Field | Description |
|---|---|
| Role | Boostlingo product/platform stakeholder |
| Goal | Understand which architecture fits which scenario |
| Cares About | Latency, quality, cost, vendor flexibility, language onboarding, operational control |
| Success | Evidence-backed recommendation |

### Non-Human Actors

| Actor | Responsibility |
|---|---|
| Browser SPA | Mic capture, mode selection, playback, rendering metrics |
| Realtime WebRTC client | Browser peer connection to OpenAI Realtime |
| .NET backend | Sessions, token broker, cascade orchestration, persistence |
| OpenAI Realtime | Direct voice-to-voice interpretation |
| Deepgram STT | Cascade speech-to-text |
| OpenAI text model | Cascade translation |
| OpenAI TTS | Cascade speech synthesis |
| JSON session writer | Disk persistence |
| Cost estimator | Estimated cost/minute |
| WER evaluator | Scripted WER scoring |
| Fake providers | Deterministic tests |

### Permission Matrix

| Actor | Can Do | Cannot Do | Risk |
|---|---|---|---|
| Browser SPA | Capture mic with permission; send audio/session events; render UI | Access standard provider API keys | Secrets leak if boundary is violated |
| Backend | Call providers; persist sessions; normalize metrics | Hide provider failures silently | Misleading comparison |
| Realtime provider | Process direct audio session | Expose internal STT/translation/TTS stages | Less observability |
| Cascade providers | Process individual stages | Own full session lifecycle | Mismatched semantics |
| Session writer | Persist whitelisted JSON | Store raw audio/secrets | Sensitive local data risk |
| Reviewer | Run app and inspect output | Modify internal provider state | Needs clear setup |

---

## Phase 3 — Stakeholders

| Stakeholder | Cares About | Would Reject If | Evidence Needed | Architecture Must Address |
|---|---|---|---|---|
| Boostlingo platform/product stakeholder | Which architecture fits which use case | Tradeoff analysis is shallow | Metrics, summary, recommendation | Dashboard, session JSON, write-up |
| Technical reviewer | Boundaries, abstractions, tests | Tangled demo code | Interfaces, fake providers, docs | Clean transport and provider seams |
| CTO/engineering manager | Feasibility and extensibility | Overbuilt or brittle MVP | ADRs and scoped decisions | MVP/deferred boundaries |
| AI/platform architect | Mode-specific tradeoffs | App hides observability differences | Per-stage metrics and provider control | Shared metric model |
| Security reviewer | API key handling | Browser exposes secrets | Server-side secret boundary | Token broker and env vars |
| Future Claude Code session | Build-ready docs | Missing contracts and anchors | Architecture anchors and scaffold | Comprehensive `ARCHITECTURE.md` |

---

## Phase 4 — User and System Flows

### Flow 1 — Configure and Start Session

- Actor: local evaluator.
- Trigger: opens SPA.
- Preconditions: frontend/backend running; env vars configured where needed.
- Steps: enter optional label → choose language direction → choose mode → start session.
- Success: backend creates session and UI is ready for turns.
- Failures: backend unavailable, missing provider config, mic denied.

### Flow 2 — Realtime Interpretation Turn

- Actor: local evaluator.
- Trigger: click Start Recording in Realtime mode.
- Steps: browser captures audio → Realtime WebRTC connection processes input → model audio/transcript events received → playback → metrics captured → turn persisted.
- Success: translated audio and transcripts appear.
- Failures: WebRTC failure, Realtime auth failure, no audio, playback error.

### Flow 3 — Cascade Interpretation Turn

- Actor: local evaluator.
- Trigger: click Start Recording in Cascade mode.
- Steps: browser captures audio → backend runs Deepgram STT → OpenAI translation → OpenAI TTS → browser playback → metrics persisted.
- Success: stage metrics, transcripts, audio, cost estimate available.
- Failures: stage timeout/failure, empty transcript, rate limit, playback failure.

### Flow 4 — Switch Mode During Session

- Actor: local evaluator.
- Trigger: changes mode toggle between turns.
- Success: same session contains turns from both modes.
- Rule: reject/disable switch while recording/processing/playing.

### Flow 5 — Run Scripted WER Evaluation

- Actor: local evaluator.
- Trigger: chooses phrase in Evaluation panel.
- Steps: reads phrase → transcript collected → backend calculates WER → result shown and persisted.
- Success: WER result attached to session.

### Flow 6 — Review Comparison Summary

- Actor: reviewer/evaluator.
- Trigger: after turns are completed.
- Steps: view average latency, estimated cost/min, WER, errors, mode breakdown.
- Success: user can explain tradeoffs with evidence.

### Flow 7 — End and Persist Session

- Actor: user/system.
- Trigger: End Session or app close.
- Steps: stop active resources → finalize summary → write JSON file.
- Success: session result is available on disk.

---

## Phase 5 — Domain Model

### Core Entities

| Entity | Definition | Source of Truth |
|---|---|---|
| `InterpretationSession` | Evaluation session containing turns and summary | Backend memory + JSON file |
| `SessionConfig` | Mode/language/provider config | Backend session state |
| `InterpretationTurn` | One recorded input and translated output | Backend session state |
| `LanguageDirection` | EN→ES or ES→EN | User-selected config |
| `InterpretationMode` | Realtime or Cascade | User-selected config |
| `TranscriptSegment` | Source/target transcript event | Provider result normalized by backend/frontend |
| `LatencyEvent` | Timestamped event | Metrics collector |
| `CostEstimate` | Estimated cost result | Cost estimator |
| `ProviderProfile` | Provider/model/pricing config | Backend config |
| `EvaluationPhrase` | Scripted phrase reference | Static JSON/config |
| `WerResult` | Scripted WER score | Backend WER evaluator |
| `ProviderError` | Normalized external error | Backend provider layer |

### Invariants

- Standard provider API keys never enter the frontend.
- Every completed turn has a mode and language direction.
- Every completed turn has at least top-level latency metrics.
- Cascade turns should have stage-level latency metrics.
- Session JSON excludes raw audio and secrets.
- WER results reference scripted phrases only.
- Cost estimates include pricing assumptions.
- Mode switches cannot occur during recording/processing/playing.

---

## Phase 6 — Requirements

### Functional Requirements

| ID | Requirement | Source | Priority | Acceptance Signal |
|---|---|---|---|---|
| REQ-F-001 | Browser SPA with mic capture | PRD | MVP | User records a turn |
| REQ-F-002 | Browser audio playback | PRD | MVP | Translated audio plays |
| REQ-F-003 | Realtime mode via OpenAI Realtime | PRD | MVP | Realtime turn completes |
| REQ-F-004 | Cascade STT→Translation→TTS mode | PRD | MVP | Cascade turn completes |
| REQ-F-005 | Mode toggle | PRD | MVP | User switches between turns |
| REQ-F-006 | English→Spanish and Spanish→English | PRD + confirmed | MVP | Both directions selectable |
| REQ-F-007 | Live transcripts | PRD | MVP | Source/target text displayed |
| REQ-F-008 | Per-stage latency display | PRD | MVP | Cascade stage metrics visible |
| REQ-F-009 | Persist session results to disk | Confirmed | MVP | JSON file created |
| REQ-F-010 | Comparison summary | Confirmed | MVP | Aggregates metrics by mode |
| REQ-F-011 | Evaluation panel with scripted WER | Confirmed | MVP | WER result displayed/stored |
| REQ-F-012 | Comparison write-up | PRD | MVP | 1–2 page recommendation exists |

### Non-Functional Requirements

| ID | Requirement | Priority |
|---|---|---|
| REQ-NF-001 | Realtime target under 1.5s speech-end to first audio | MVP benchmark |
| REQ-NF-002 | Cascade under 3s, target under 2s | MVP benchmark |
| REQ-NF-003 | 5-minute demo stability | MVP benchmark |
| REQ-NF-004 | Local-first setup | MVP |
| REQ-NF-005 | Optional AWS path | Stretch |

### Integration Requirements

| ID | Requirement | Priority |
|---|---|---|
| REQ-I-001 | Backend owns provider secrets | MVP |
| REQ-I-002 | Realtime ephemeral credential endpoint | MVP |
| REQ-I-003 | Deepgram STT provider | MVP |
| REQ-I-004 | OpenAI translation provider | MVP |
| REQ-I-005 | OpenAI TTS provider | MVP |
| REQ-I-006 | Fake provider implementations | MVP |

### Testing Requirements

| ID | Requirement | Priority |
|---|---|---|
| REQ-T-001 | Cascade orchestrator success path | MVP |
| REQ-T-002 | Provider failure paths | MVP |
| REQ-T-003 | WER calculator tests | MVP |
| REQ-T-004 | Cost estimator tests | MVP |
| REQ-T-005 | Session persistence tests | MVP |

### Deferred Requirements

- Multi-user call rooms.
- Auth/accounts.
- Raw audio persistence.
- Multiple real providers per stage.
- Seamless in-flight mode migration.
- Full semantic translation scoring.
- Full production AWS deployment.

---

## Phase 7 — Constraints, Evaluation, and Timebox

### Constraints

- 3–4 days / 15–20 hours.
- TypeScript frontend preferred.
- .NET/C# backend preferred.
- Local-first.
- Optional deployment after local success.
- Must use OpenAI Realtime for Realtime mode.
- Must implement cascade with provider abstractions.
- Must document coding-agent usage.
- Must maintain meaningful git history.

### Evaluation Criteria

- Both modes demoable.
- Metrics visible and persisted.
- Code boundaries clean.
- Provider abstractions credible.
- Tests cover critical paths.
- Final write-up makes a defensible recommendation.

---

## Phase 8 — MVP-Scoped Inferences

| Inference | Classification | Architecture Impact |
|---|---|---|
| Backend relay/orchestrator is required | MVP-critical | Secrets, cascade orchestration, persistence |
| Shared session model is required | MVP-critical | Comparable evidence across modes |
| Shared metrics schema is required | MVP-critical | Fair latency/cost comparison |
| Provider fakes are required | MVP-critical | Deterministic tests |
| JSON persistence is enough | MVP simplification | No database |
| No raw audio persistence | MVP simplification | Lower privacy/storage risk |
| Cost is estimated | MVP simplification | Config-driven calculator |
| WER is scripted | MVP simplification | Phrase fixtures and evaluator |
| Multi-user live calls are deferred | Deferred | Single local evaluator only |

---

## Phase 9 — Assumptions and Open Questions

### Assumptions

| Assumption | Status | Fallback |
|---|---|---|
| OpenAI Realtime WebRTC works from browser with ephemeral token | Researched | Backend proxy if blocked |
| Deepgram is feasible for streaming STT | Researched | OpenAI STT fallback |
| OpenAI text model is adequate for EN/ES translation | Researched | DeepL/Anthropic later |
| OpenAI TTS is adequate for streaming TTS | Researched | Deepgram Aura/ElevenLabs later |
| Pricing can be estimated from config | Researched | Write-up-only if UI weak |
| Local-first is acceptable | Confirmed and PRD-supported | Deploy if time remains |

### Remaining Open Questions for Implementation

- Exact frontend framework selection: React/Vite is recommended but can be finalized by Claude Code.
- Exact OpenAI model names: use config and current account availability.
- Exact audio recording format for cascade: choose simplest browser format accepted by backend/provider path; document if transcoding is deferred.
- Whether Realtime transcripts are reliably available for selected model/session config; if not, UI should show unavailable for that metric.
