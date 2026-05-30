# Session 008 — Phase E backend (E.1 broker + E.2b realtime cost + E.5-backend stale-flush)

- **Date:** 2026-05-30
- **Phase:** E (backend, `server/`) — the Realtime backend obligations: ephemeral-credential broker (E.1), realtime per-turn cost wiring (E.2b), stale-session auto-end/flush (E.5-backend). **Backend Phase E is now COMPLETE.**
- **Area:** `server/` (.NET 8 / C# backend)
- **Predecessor session:** [007 — Phase D close (D.6 panels + D.7 error banner / component tests)](007-2026-05-29-phase-d-d6-d7.md)
- **Successor session:** _(Phase E frontend — E.3 browser WebRTC client; a fresh `web/` implementer cycles in. Backend→frontend transition at this round-seal.)_
- **Preflight at close:** GREEN — `dotnet format --verify-no-changes` (clean) · `dotnet build` (0 warnings / 0 errors, warnings-as-errors) · `dotnet test` **316/316**. (Frontend untouched this session.)

---

## Why this session existed

Phase D closed the frontend cascade path; Phase E is the second highest-risk integration (Realtime, browser WebRTC + a backend-minted ephemeral credential). This session built the **deterministic backend seams** Realtime needs, BEFORE the frontend WebRTC work (E.3) — all TDD'd against fakes / mock `HttpClient`, no live OpenAI key:

1. **E.1** — the ephemeral-credential broker (the first slice where safety invariants #1/#2 go live in a request path).
2. **E.2b** — wiring the already-implemented `CostEstimator.EstimateRealtime` (zero callers) into the realtime `/complete` path so realtime turns carry a cost for the cross-mode comparison.
3. **E.5-backend** — the Flow-H stale-session auto-end/flush so an abandoned (browser-refreshed) session still produces its JSON artifact.

**E.2 (turn-events ingest)** required no code this round — it was already delivered by **B.9c-ii** (`POST …/turns/{turnId}/events` + backend-owned turnId). Noted for traceability; no slice.

---

## What was built

Three slices, one commit each (E.1 is a SAFETY slice → its own commit by rule). Suite 284 → **316** (+32).

### E.1 — Realtime ephemeral-credential broker (`348ea76`, SAFETY) — brief 035

`POST /api/realtime/client-secret` mints a short-lived OpenAI Realtime client secret (`ek_…`) by calling the GA `POST https://api.openai.com/v1/realtime/client_secrets` with the standard key server-side, returning ONLY the ephemeral secret + expiry + resolved model. The §18/§20 pattern applied to a non-streaming upstream call: thin `HttpClient` shell + pure mapper. +20 tests (284→304).

**Files created:**
- `Realtime/RealtimeClientSecretService.cs` — transport shell (Bearer + `OpenAI-Safety-Identifier`; linked-CTS timeout over send+read; one bounded 429 retry honoring `Retry-After` with an injected delay; fail-closed model-allowlist + missing-key short-circuits).
- `Realtime/RealtimeClientSecretMapping.cs` — pure `internal static`: GA request-body build, interpreter-instructions render, **dual-shape** response parse, epoch→ISO-8601, exception→`ProviderError`.
- `Realtime/RealtimeModels.cs` — `RealtimeTokenRequest`/`RealtimeTokenResponse` DTOs, `RealtimeMintOutcome`, `RealtimeModelCatalog` (`{gpt-realtime, gpt-realtime-mini}`).
- `Controllers/RealtimeController.cs` — thin controller (§16 string caps → 400; outcome → `Ok`/sanitized `UiError` mirroring `CascadeController`).
- Tests: `RealtimeClientSecretMappingTests` (5), `RealtimeClientSecretServiceTests` (11), `RealtimeControllerTests` (4).

**Files modified:** `Program.cs` (unconditional `AddHttpClient<RealtimeClientSecretService>(BaseAddress=https://api.openai.com/)`).

### E.2b — Realtime per-turn cost wiring (`a6bc540`) — brief 036

On realtime `/complete`, the finalized turn's `CostEstimate` is populated via the existing `CostEstimator.EstimateRealtime` (no math re-implemented), inside the idempotent `FinalizeTurn` transform. Cascade turns untouched (WS-priced). +7 tests (304→311).

**Files modified:**
- `Sessions/SessionService.cs` — injected `CostEstimator`; `CompleteTurnAsync` branches `Mode==Realtime` → `EstimateRealtimeCost` helper (input seconds from merged `turn.AudioDurationMs`; output from the new optional field; degrades to null on Unavailable / unreported input duration).
- `Sessions/SessionDtos.cs` — added optional `CompleteTurnRequest.OutputAudioDurationMs?` (`[Range]`-bounded; absent → output disclosed-unavailable in `Assumptions`).

**Files created:** `RealtimeTurnCostTests` (7).

### E.5-backend — stale-session auto-end/flush (`f007a72`) — brief 037

Flow H (ARCH-017): creating a new session flushes any prior un-ended (refreshed/abandoned) session through the existing `EndAsync` finalize+persist seam, so it still produces its JSON artifact. +5 tests (311→316).

**Files modified:**
- `Sessions/SessionStore.cs` — new pure `ActiveSessionIds()` (snapshot of un-ended session ids) + `using System.Linq`.
- `Sessions/SessionService.cs` — `Create` → async `CreateAsync` (flush priors FIRST, then register); `FlushStaleSessionsAsync` reuses `EndAsync` with `CancellationToken.None`; injected `ILogger<SessionService>`; `ISessionService.Create` → `CreateAsync`.
- `Controllers/SessionsController.cs` — `Create` action now async, awaits `CreateAsync`.
- `Sessions/.../RealtimeTurnCostTests.cs` (E.2b test) — mechanical `Create`→`await CreateAsync` + `NullLogger` ctor arg (approved caller update).

**Files created:** `StaleSessionFlushTests` (5).

---

## Decisions made

- **E.1 GA response is TOP-LEVEL, not nested (dual-shape parse).** The brief described a nested `client_secret.{value,expires_at}`, but the GA `client_secrets` endpoint returns `{value, expires_at, session}` top-level (the nested shape is the legacy `/v1/realtime/sessions` Beta). Orchestrator verified via Context7. `ParseResponse` is tolerant of BOTH (top-level primary, nested fallback) + tested both — correct regardless of which the live API returns (the §18/§20 "verify the real API shape" discipline). Orchestrator corrects ARCH-010 §7 (documented the wrong shape).
- **E.1 missing-key → `realtime.auth` (401), non-retryable, no upstream call.** Honest retry semantic (a missing server key is not transient) + reuses `MapStatus(401)`, no bespoke code. Broker DI'd unconditionally (reads the key at call time).
- **E.1 model allowlist at the mint boundary** via a new area-owned `RealtimeModelCatalog` — off-catalog → 400 fail-closed before upstream. Discharges the realtime-model half of the B.9c-i model-allowlist carry-forward.
- **E.2b output-audio sourcing — fork (a):** added optional `CompleteTurnRequest.OutputAudioDurationMs?` (E.4 reports it). `EstimateRealtime` treats absent output as 0 with no disclosure, so the **wiring layer** appends an "output not reported" assumption when absent — never a silent 0 (streaming-honesty).
- **E.2b basis is `"tokens"`, not `"audioSeconds"`.** The brief conflated the input unit (audio-seconds, which live in `Units`) with the `PricingBasis` string. `EstimateRealtime` correctly returns `"tokens"` (realtime bills on audio tokens). Asserted reality; no math change. Orchestrator fixes the brief/MVP_TASKS wording.
- **E.2b zero/unreported input duration → null, not a synthetic $0.00.** A realtime turn completing with no `audioDurationMs` (`CreateTurn`'s 0 default) carries no usable audio signal → degrade to null (lesson §9/§12). Review-surfaced; pinned by a dedicated test.
- **E.5 `Create` → async `CreateAsync`, flush-first.** The flush must `await EndAsync` (persisting the abandoned session is the point) and the test must see it complete → async is forced. Flush BEFORE registering the new session so it can't self-flush. Keeps orchestration in the service (ARCH-008), not the controller. Approved file-set expansion (controller + interface + the E.2b test caller).
- **E.5 owed write uses `CancellationToken.None`.** Review-caught: passing the request token to `EndAsync`→`WriteAsync` (whose catch-filter EXCLUDES OCE) meant a client cancelling `POST /api/sessions` mid-flush would leave the stale session ended-in-memory but **artifact-less** AND the new session unregistered — the exact half-state the flush prevents. The abandoned session's owed artifact write must not be abortable by the new request.
- **E.5 flush-all-un-ended + degrade+log.** Defensively flush every un-ended prior (cheap, robust to leaks); persist-failure swallowed + logged (`ILogger`, single-lined per §13), `EndedAt` still flips, new `Create` always succeeds (§11).

---

## Decisions explicitly NOT made (deferred)

- **Frontend TS mirror of `outputAudioDurationMs`** — E.4's surface (the frontend measures played output audio + reports it). Carry-forward → E.4.
- **`RealtimeModelCatalog` ↔ `ConfigService.RealtimeModels` unify** — `ConfigService` keeps its own private copy; deliberately NOT refactored inside a SAFETY slice. Carry-forward → G / opportunistic.
- **No auth/authz gate on `POST /api/realtime/client-secret`** — any reachable client consumes standard-key quota + gets an `ek_`. Project-wide single-trusted-user posture (ARCH-002/019), not a slice regression. Carry-forward → G hardening.
- **Cached-audio-input seconds (realtime prompt caching)** not sourced at `/complete`; `EstimateRealtime` handles it if supplied — a later accuracy refinement. Carry-forward.
- **TTL/timer-based stale-session sweep** for true multi-session / long-idle abandonment — on-create flush covers the single-user demo path. Carry-forward → G hardening.

---

## TDD compliance

**Clean — all three slices test-first.** Each followed `/tdd`: RED tests written + sent for Step-2.5 orchestrator review BEFORE any behavior, confirmed RED, then GREEN. The compile-surface scaffolding (signature stubs / `NotImplementedException` / no-op flush stub referencing fields) is the standard C# "exists-but-lacks-behavior" RED pattern — the *behavior* was driven by the tests, not back-filled. No TDD violations. No safety-critical TDD skips (E.1's two invariant sentinels + the GA-target assertion were authored as RED acceptance criteria).

---

## Reachability (Step 7.5 — all satisfied)

- **E.1:** `POST /api/realtime/client-secret` → `MapControllers` → `RealtimeController.Mint` → `RealtimeClientSecretService.MintAsync`. The `mint_endpoint_returns_200_token_shape` wire test exercises the real route end-to-end (`WebApplicationFactory`).
- **E.2b:** `POST /api/sessions/{id}/turns/{turnId}/complete` → `SessionsController.CompleteTurn` → `SessionService.CompleteTurnAsync` → realtime branch → `EstimateRealtime`. The new `CostEstimator` injection is DI-proven by the full suite (`SessionsControllerTests` boot via `WebApplicationFactory`).
- **E.5-backend:** `POST /api/sessions` → `SessionsController.Create` → `await CreateAsync` → `FlushStaleSessionsAsync` (unconditional first line). 1-hop trace; async `CreateAsync` + `ILogger` DI-proven by the suite.

No tested-but-unwired gaps.

---

## Open follow-ups

### Step-9 categorized routing (orchestrator writes hot; surfaced here for `/orchestrate-end` verification)

- **Architecture-doc notes (ARCHITECTURE.md):** ARCH-010 §7 GA response is **top-level** `{value, expires_at, session}` (was documented nested — correction); ARCH-010 mint body keeps `audio.input.transcription` enabled (PRD must-have 6 source transcript); ARCH-014 realtime cost requires a reported audio duration (0 → null, not $0.00); ARCH-017 Flow-H realization (on-`Create`, all-un-ended, via `CreateAsync`, reuse `EndAsync`, degrade+log).
- **Cross-doc invariant change (ARCH-009 + Appendix A + `server/CLAUDE.md` row):** `CompleteTurnRequest.OutputAudioDurationMs?` **added** (E.2b). The one genuine DTO field add this round — orchestrator-written. **Also verify Appendix A lists the E.1 realtime token DTOs** (`RealtimeTokenRequest`/`RealtimeTokenResponse`, mirror-registration). `ISessionService.Create`→`CreateAsync` is an internal service-contract signature change (no ARCH-009 wire change, no model field) → no doc row needed.
- **Convention lessons (`server/LESSONS.md`):** §25 (realtime cost wired at `/complete`, reuse `EstimateRealtime`, degrade-to-null, modes price at their own terminal); §26 (stale-flush reuses the `/end` seam on `Create`, `CancellationToken.None` for owed writes, degrade+log). Plus the anticipated E.1 broker convention (§18/§20 for a non-streaming upstream + dual-shape parse). _(Numbers per the orchestrator's routing.)_
- **Carry-forwards (next-brief / G):** E.4 reports `outputAudioDurationMs` + its TS mirror; `RealtimeModelCatalog`↔`ConfigService` unify; no-auth-gate; cached-audio-input seconds; TTL stale-sweep; B.9c-i model-allowlist updated (realtime half discharged).

### Wiring follow-ups
None — all three features reachable from real production routes.

---

## How to use what was built

- **Mint a realtime credential:** `POST /api/realtime/client-secret` `{ sessionId, direction:{source,target}, model? }` → `{ clientSecret:"ek_…", expiresAt, model }`. E.3's browser WebRTC client GETs this before the SDP handshake. The standard key never leaves the server; the `ek_…` is the only credential the browser sees.
- **Realtime turn cost:** a realtime `/complete` now returns a turn with a populated `CostEstimate` (when `audioDurationMs` is reported + pricing is configured). E.4 should send `outputAudioDurationMs` to price the output side.
- **Stale flush:** automatic — any `POST /api/sessions` ends + persists prior abandoned sessions. No client action needed (Flow H / refresh recovery).
