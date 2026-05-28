# DECISIONS.md — AI Interpreter Workbench

> **Status:** ADR-style decision log for MVP architecture.
>
> **Decision states:** Locked unless explicitly marked proposed/deferred.

---

## Locked Decision Summary

| Area | Decision | Status | Rationale | Fallback |
|---|---|---|---|---|
| Product posture | Architecture evaluation workbench, not production platform | Locked | Reviewer needs tradeoff proof, not full product | Narrow to local-only demo if time slips |
| UX | Turn-based click start/stop | Locked | Reduces audio drift/duplex complexity | Hold-to-talk later |
| Language selection | Explicit English→Spanish and Spanish→English | Locked | Satisfies PRD minimum and avoids auto-detect complexity | Auto direction later |
| Realtime transport | Browser WebRTC + backend ephemeral credential | Locked | Best browser audio fit; keeps standard key server-side | Backend WebSocket proxy |
| Cascade STT | Deepgram streaming STT | Locked | Streaming STT, interim results, endpointing, vendor diversity | OpenAI STT |
| Cascade translation | OpenAI text model | Locked | Lowest integration risk, stream-capable boundary | DeepL/Anthropic later |
| Cascade TTS | OpenAI TTS | Locked | Streaming-capable and low integration overhead | Deepgram Aura / ElevenLabs |
| Provider scope | One real provider per stage + interfaces + fakes | Locked | Meets PRD without overbuilding | Add second provider if time remains |
| Backend | .NET/C# | Locked | PRD preferred language | Node/Python only if blocker |
| Frontend | TypeScript SPA | Locked | PRD preferred language | Keep framework minimal |
| Persistence | Local JSON session files | Locked | Inspectable evidence without DB | SQLite later |
| Audio persistence | Do not save raw audio | Locked | Reduces privacy/storage scope | Optional later |
| Metrics | Shared latency event schema | Locked | Enables fair comparison | Top-level only if stage blocked |
| Cost | Config-driven live estimate | Locked | Supports write-up without billing-grade complexity | Write-up-only estimate |
| WER | Backend scripted WER utility | Locked | Deterministic STT quality check | Manual transcript review |
| Deployment | Local-first; optional AWS later | Locked | PRD allows local-only | AWS after demo works |

---

## ADR-001 — Product Posture

**Status:** Locked

### Context

Boostlingo needs to compare two architectural approaches for live interpretation. The challenge is to build both, instrument them, and form a defensible opinion.

### Options Considered

| Option | Pros | Cons | PRD Alignment |
|---|---|---|---|
| Architecture evaluation workbench | Directly supports comparison and write-up | Not production app | Strong |
| Production interpretation platform | More realistic long-term | Impossible in timebox | Weak |
| Thin API demo | Fast | Does not prove architecture tradeoffs | Weak |

### Decision

Build an architecture evaluation workbench.

### Rationale

The MVP must prove engineering judgment, not product completeness.

### Fallback

If time slips, reduce UI polish but preserve both modes, metrics, and persistence.

---

## ADR-002 — UX Interaction Model

**Status:** Locked

### Context

Live interpretation can be always-on/full-duplex, push-to-talk, or turn-based. The PRD requires browser microphone capture and live interpretation but the timebox is only 15–20 hours.

### Options Considered

| Option | Pros | Cons | Build Risk | Demo Risk |
|---|---|---|---|---|
| Click start/stop turns | Simple, predictable, easy to measure | Less production-like | Low | Low |
| Hold-to-talk | Familiar interaction | Mouse/keyboard interaction risk | Medium | Medium |
| Always listening/VAD | More live | Audio drift/VAD/barge-in complexity | High | High |

### Decision

Use click-to-start/click-to-stop turn-based interpretation.

### Tradeoff

This simplifies full-duplex interpretation but increases reliability and measurement clarity.

---

## ADR-003 — Realtime Transport

**Status:** Locked

### Context

Realtime mode must use OpenAI Realtime. Browser audio is captured and played directly in the SPA.

### Options Considered

| Option | Pros | Cons | Build Risk | Security Risk | PRD Alignment |
|---|---|---|---|---|---|
| Browser WebRTC + backend ephemeral credential | Official browser pattern, lower latency, keeps standard key server-side | Frontend WebRTC complexity | Medium | Low | Strong |
| Backend full WebSocket proxy | More server control | More latency, more audio handling backend complexity | High | Low | Medium |
| Browser direct with standard key | Simple | Exposes API key | Low | Critical | Invalid |

### Decision

Use browser WebRTC with backend-minted ephemeral OpenAI Realtime credentials.

### Fallback

If WebRTC is blocked, use backend WebSocket proxy as emergency fallback.

---

## ADR-004 — Cascade STT Provider

**Status:** Locked

### Context

Cascade mode requires STT. It should demonstrate composability and provide useful stage-level latency signals.

### Options Considered

| Option | Pros | Cons | Build Risk | Demo Risk | PRD Alignment |
|---|---|---|---|---|---|
| Deepgram | Streaming STT, interim results, endpointing, .NET SDK path | Extra vendor key | Medium | Medium | Strong |
| OpenAI STT | Fewer providers | Less provider diversity | Low | Medium | Medium |
| AssemblyAI | Strong STT | More integration research | Medium | Medium | Medium |
| Soniox | Multilingual/live focus | More uncertainty | Medium | Medium | Medium |

### Decision

Use Deepgram as the first real `ISttProvider`.

### Fallback

Use OpenAI STT if Deepgram setup blocks progress.

---

## ADR-005 — Cascade Translation Provider

**Status:** Locked

### Context

Cascade mode needs text translation from source transcript to target transcript.

### Options Considered

| Option | Pros | Cons | Build Risk | PRD Alignment |
|---|---|---|---|---|
| OpenAI text model | Simple, already needed provider, stream-shaped possible | More OpenAI dependency | Low | Strong |
| DeepL | Translation-specialized | New API and character billing; less streaming-friendly | Medium | Strong |
| Anthropic | Strong LLM, streaming | New key/provider; overkill | Medium | Medium |

### Decision

Use OpenAI text model as first real `ITranslationProvider`.

### Fallback

Use DeepL later for quality-focused translation provider comparison.

---

## ADR-006 — Cascade TTS Provider

**Status:** Locked

### Context

Cascade mode needs translated text to become playable audio.

### Options Considered

| Option | Pros | Cons | Build Risk | PRD Alignment |
|---|---|---|---|---|
| OpenAI TTS | Low integration overhead, same provider account, streaming-oriented | More OpenAI dependency | Low | Strong |
| Deepgram Aura | Speech-specialized, streaming TTS | Less provider diversity if paired with Deepgram STT | Medium | Strong |
| ElevenLabs | High voice quality | Extra integration and cost | Medium | Medium |
| Azure/Polly | Enterprise-grade | More setup overhead | High | Medium |

### Decision

Use OpenAI TTS as first real `ITtsProvider`.

### Fallback

Use Deepgram Aura TTS if OpenAI TTS blocks progress.

---

## ADR-007 — Provider Scope

**Status:** Locked

### Context

PRD requires provider abstractions but does not require multiple real providers per stage.

### Options Considered

| Option | Pros | Cons |
|---|---|---|
| One real provider per stage + interfaces + fakes | Meets requirement within timebox | Less provider demo breadth |
| Multiple real providers per stage | Stronger swap demo | Too much integration scope |
| No abstraction | Fast | Violates PRD |

### Decision

Implement one real provider per stage, clean interfaces, fake providers for tests, and documented extension path.

---

## ADR-008 — Persistence

**Status:** Locked

### Context

User wants results persisted to disk. The MVP needs evidence for the write-up.

### Options Considered

| Option | Pros | Cons |
|---|---|---|
| JSON files | Simple, inspectable, no DB | Limited querying |
| SQLite | Queryable | Schema/migration overhead |
| No persistence | Fastest | Rejected; weak evidence |

### Decision

Persist session metadata, transcripts, metrics, errors, cost estimates, WER results, and summary to JSON files. Do not persist raw audio.

---

## ADR-009 — Metrics and Cost

**Status:** Locked

### Context

The PRD names latency and cost per minute as impact metrics.

### Decision

Use a normalized `LatencyEvent` schema and a config-driven cost estimator. Display **Estimated cost/min** live and persist assumptions.

### Tradeoff

Costs are not billing-grade. This is acceptable if clearly labeled and documented.

---

## ADR-010 — WER Evaluation

**Status:** Locked

### Context

The PRD names WER as an interpretation quality metric, but WER requires known reference text and is most directly applicable to STT transcript accuracy.

### Decision

Use scripted phrase references and backend C# WER utility for STT quality checks.

### Tradeoff

WER does not measure semantic translation quality. Overall interpretation quality must be discussed through transcript review, subjective notes, latency, and controllability.

---

## ADR-011 — Deployment

**Status:** Locked

### Context

The PRD encourages deployment but allows local-only with clear setup instructions.

### Decision

Build local-first. Include optional AWS deployment notes but do not let deployment drive architecture until local demo is stable.

### Fallback

If deployment is attempted, prefer a simple static frontend + containerized backend approach.

