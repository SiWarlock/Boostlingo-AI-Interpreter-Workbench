# /tdd brief — dev_tts_synthesis_endpoint

## Feature
A **dev-only** backend endpoint that synthesizes a single text utterance to speech audio via the existing OpenAI TTS provider and returns the complete audio bytes — so the G.4 soak-harness (browser) can generate + cache the scripted EN/ES conversation audio **without the OpenAI key ever reaching the browser** (invariant #1). Wraps `ITtsProvider.SynthesizeAsync`, collects the streamed chunks into one payload, returns it.

## Use case + traceability
- **Task ID:** G.4-BE (the 5-min synthetic soak-harness — backend half).
- **Architecture sections it implements:** `ARCHITECTURE.md §9 / ARCH-012` (provider contracts — `ITtsProvider`), `§15 / ARCH-020` (the 5-min stability preflight this harness automates), `§5 / ARCH-008` (thin controller → service), `§15 / ARCH-019` (boundary hygiene). Cost/voice: `§11 / ARCH-014`, `ARCH-011` (TTS input cap).
- **Related context:** `docs/g4-harness-design.md` (the full harness design + the 4 user-settled sub-decisions). The harness consumes this endpoint to cache audio (generate-on-first-run + gitignore — script text is the committed source of truth). This is the ONLY new production-reachable surface in G.4; everything else is FE harness code.

## Why a new endpoint is REQUIRED (not optional)
TTS is reachable today **only inside the cascade orchestrator** (the WS streaming path) — there is no standalone synthesis route (verified: the 15 existing routes have no TTS endpoint). Invariant #1 keeps the OpenAI standard key server-side only, so the browser **cannot** call OpenAI TTS directly. The harness must therefore request synthesis from the backend. The provider already enforces a 4096-char input cap (`OpenAiTtsMapping.MaxInputChars`), so abuse surface is pre-bounded.

## Acceptance criteria (what "done" means)
- [ ] A new endpoint accepts `{ text, language }` (language ∈ `en`/`es`) and returns the **complete synthesized audio bytes** with the correct `Content-Type` on success.
- [ ] Provider logic lives in a **thin service** (e.g. `IDevTtsSynthesisService`), NOT in the controller (ARCH-008 / forbidden pattern #2). The controller maps the service outcome → HTTP at the boundary.
- [ ] The service collects `TtsAudioChunk.Bytes` in **`Seq` order** into one contiguous payload; returns `(bytes, contentType)` where contentType comes from `TtsFirstAudio`/`TtsComplete` (the real header value).
- [ ] A provider `TtsFailed` → a **sanitized `UiError`** (reuse `ErrorSanitizer`; no raw provider payload/secret/stack — invariant #4); the input-cap-exceeded path (`OpenAiTtsMapping.CapExceeded`) maps to a clean 400-class `UiError`.
- [ ] Voice resolves by language: pass an **empty `Voice`** so `OpenAiTtsMapping.ResolveVoice` selects `VoiceByLanguage[language]` (the Phase-J §38 pattern); `Model`/`ResponseFormat` from `OpenAiTtsOptions`.
- [ ] **Dev-only gating:** the endpoint is unreachable (404 / not-mapped) when the host is NOT `Development` (see Step-2.5 Q1). Pinned by a test.
- [ ] No secret/raw-session-audio persistence concern: this endpoint **does not** touch `SessionStore`/`SessionPersistenceWriter` (stateless synthesis; nothing persisted — structural, like §28 transcribe).
- [ ] All unit tests in `server/AiInterpreter.Tests/` pass; `/preflight` clean.

## Wiring / entry point (Step 7.5)
The new HTTP route (`POST /api/dev/tts` or equivalent per Q1). Confirm it is reachable from the routing table in `Development` and 404/unmapped outside it — and that the G.4-FE harness fetch target matches. The service is constructed via DI (`ITtsProvider` injected — DI-swaps real↔fake by the existing `USE_FAKE_PROVIDERS`/key-presence wiring, so tests run against `FakeTtsProvider`).

## Files expected to touch
**New:**
- `server/AiInterpreter.Api/<area>/DevTtsSynthesisService.cs` (+ `IDevTtsSynthesisService`) — collects the `ITtsProvider` stream into `(byte[], contentType)`; degrade `TtsFailed`/cap → outcome.
- the dev endpoint (a thin `DevController` OR a Development-gated minimal-API map — Q1).
- `server/AiInterpreter.Tests/DevTtsSynthesisServiceTests.cs` (+ an endpoint/wire test via `WebApplicationFactory`).

**Modified:**
- `Program.cs` — register the service + (if minimal-API) the Development-gated endpoint map; or controller registration.

If implementation needs files beyond this list, **flag at Step 2.5** before going GREEN.

## RED test outline (Step 2)
Tests in `server/AiInterpreter.Tests/DevTtsSynthesisServiceTests.cs` (+ a thin wire test):

1. **`synthesize_collects_chunks_in_seq_order`** — `FakeTtsProvider` (ChunkedThenComplete, 3 chunks) → service returns the concatenated bytes in `Seq` order + the content-type from the stream. Why: ARCH-012 streaming contract; the harness needs the whole payload.
2. **`synthesize_resolves_voice_by_language`** — language `es` builds a `TtsRequest` with empty `Voice` (so `ResolveVoice` picks `VoiceByLanguage["es"]`) + `Model`/`ResponseFormat` from options. Why: §38 voice-by-target pattern; fair EN-voice/ES-voice synthesis.
3. **`synthesize_provider_failure_degrades_sanitized`** — `FakeTtsProvider(Error)` → a sanitized failure outcome (no raw provider message; reuse `ErrorSanitizer`). Why: invariant #4 / §13.
4. **`synthesize_input_over_cap_rejected_clean`** — text > 4096 chars → clean 400-class `UiError` (the `CapExceeded` event path), no partial bytes. Why: ARCH-011/019 bound.
5. **`endpoint_dev_only`** (wire) — in `Development` the route returns 200 + audio bytes (fake provider); outside `Development` it is 404/unmapped. Why: dev-only surface, keep the production/demo build clean (the harness is a dev tool).
6. **`endpoint_stateless_no_persistence`** — after a synth call, `SESSION_DATA_DIR` is untouched + no `SessionStore`/writer dependency (structural). Why: invariant #3/#5 — synthesis persists nothing (§28 precedent).

## Cross-doc invariant impact (implementer flags at Step 9; orchestrator writes the docs)
- **Model field changes:** none (reuses `TtsRequest`/`TtsEvent`/`OpenAiTtsOptions` unchanged).
- **Orchestrator doc rows to write hot (Step 9 routing):** a new **API route row** (ARCH-009 §6 + the routing manifest) for the dev TTS endpoint, noted **dev-only**; an ARCH-020 realization note that the 5-min preflight is now also driven by a synthetic harness (rides the owed Phase-J ARCH-doc-sync pass). No Appendix-A contract-model row (no model change). The new `IDevTtsSynthesisService` is an internal service, not a cross-doc contract.

## Things to flag at Step 2.5
1. **Dev-only gating mechanism.** (a) a Development-gated **minimal-API endpoint** (`if (app.Environment.IsDevelopment()) app.MapPost(...)`) — a true gate, never registered in prod, but a style-outlier vs the `[ApiController]` controllers; (b) a `[ApiController] DevController` that **returns 404 when `!IsDevelopment()`** — consistent controller style + testable, but the type is always present; (c) no gate (rely on the single-trusted-user/no-auth posture + the 4096 cap). My default vote: **(a) minimal-API, Development-gated** — it keeps the synth surface out of the production/demo build entirely (matches the harness's dev-only design constraint), and a single minimal-API map is a small, contained outlier. Flag if you'd rather keep controller-consistency (b).
2. **Response shape — raw bytes vs base64 JSON.** Default: **raw bytes** via `File(bytes, contentType)` (the harness does `res.arrayBuffer()` → `AudioContext.decodeAudioData`). Vote: **raw bytes** — simplest for the browser decode; no base64 bloat.
3. **`ResponseFormat` — wav vs the configured mp3.** Default `OpenAiTtsOptions.ResponseFormat` is `mp3`. The harness decodes via `decodeAudioData` (Chrome handles both). Vote: **request `wav` explicitly** for clean/fast lossless decode + deterministic injection (override the option for this endpoint), unless you see a reason to honor the configured mp3. Minor.
4. **`SessionId`/`TurnId` on the synthetic `TtsRequest`.** No real turn exists. Default: pass constant placeholders (e.g. `"soak"`/`"synth"`). Vote: **constant placeholders** — they're only used for provider-side request shaping/logging, never persisted here.

## Dependencies + sequencing
- **Depends on:** nothing new — `ITtsProvider`/`FakeTtsProvider`/`OpenAiTtsOptions`/`ErrorSanitizer` all exist.
- **Blocks:** the G.4-FE harness audio-injection slice (caches this endpoint's output). Parallel-safe with the G.4-FE deterministic-core slice (087), which has no dependency on this.

## Estimated commit count
**1.** One focused dev-only synthesis endpoint + its service. No safety-invariant pin (synthesis persists nothing; the key stays server-side as it already does for the cascade). Not bundled with anything (cross-area from the FE harness; standalone concern).

## Lessons-logged candidates anticipated
- **Convention candidate** — "a dev-only harness/synthesis surface is gated out of the production build (minimal-API Development-map) and stays stateless (no `SessionStore`/writer dep) — synthesis persists nothing, invariant #3 structural" (extends §28's stateless-transcribe pattern to a dev tool).
- **Architecture-doc note candidate** — the ARCH-020 5-min preflight is now driven by a synthetic harness; the dev TTS route is the one new (dev-only) surface.

## How to invoke
1. Read this brief end-to-end + skim `docs/g4-harness-design.md` (the harness context).
2. Run `/tdd dev_tts_synthesis_endpoint` in the backend implementer session.
3. Step 0 (Restate) → confirm against the Feature line.
4. Step 2.5 → send the test write-up + answers to the 4 questions above (or take defaults).
5. Step 9 → surface the new-route cross-doc row + any lesson candidate.
