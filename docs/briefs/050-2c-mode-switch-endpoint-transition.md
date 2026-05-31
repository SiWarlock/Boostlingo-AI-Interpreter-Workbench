# /tdd brief — mode_switch_endpoint_and_transition (Finding 2c, Option B)

> **Real-key-smoke bug-fix (HIGH — comparison-invalidating) + a deferred-carry-forward discharge.** Cross-area (backend + frontend). **USER chose Option B** (2026-05-31, via lead): a backend mode-switch endpoint that updates the session's `CurrentMode` **and** wires the built-but-unwired `RecordModeTransition` / `ModeTransitionEvent` (the Flow-G timeline). User picked the fuller fix because the transition timeline **pairs with the requested session-history view (H.3)** — so H.3 will have timeline data.

## Feature
Make a turn's `Mode` accurate after a **mid-session mode switch** by (a) adding `POST /api/sessions/{id}/mode` that validates + updates `config.CurrentMode` and records a `ModeTransitionEvent`, and (b) having the frontend `ModeToggle` call it on a switch. This fixes Finding 2c (realtime turns counted as cascade) and discharges the Phase-E Flow-G carry-forward.

## Root cause (confirmed, Finding 2c)
`POST /api/sessions/{id}/turns` → `CreateTurn(string id)` takes **no mode** — it stamps the turn with the session's `config.CurrentMode`. There is **no mode-switch endpoint** (13-route manifest), and `ModeToggle` is **frontend-only** (`setMode` → store, no backend call). So after a mid-session toggle, the backend `CurrentMode` is stale → turns get the wrong mode → the by-mode comparison is invalid. (The session-create already sets the *initial* mode correctly; only mid-session switches are broken.)

## Use case + traceability
- **Task ID:** Finding 2c (smoke bug-fix) + discharges the Phase-E `ModeTransitionEvent`-persistence carry-forward.
- **Architecture:** `ARCH-009` (new session route), `ARCH-010` / `ARCH-017` (Flow G mode transition + timeline), `ARCH-005` (`ModeTransitionEvent` record — already defined, `SessionModels.cs`), `ARCH-007` (frontend clean-separation).

## Shared contract (BOTH impls build to this — defined here so the halves align)
- **Endpoint:** `POST /api/sessions/{id}/mode`
- **Request DTO `SetModeRequest`:** `{ mode: InterpretationMode }` (the TARGET mode; `"cascade" | "realtime"`). The backend derives the rest of the transition server-side.
- **Response:** `200` with the **updated `InterpretationSession`** (mirrors `Create`'s shape so the frontend can resync `config.currentMode`), or `404` `session.not_found` (unknown id), or `400` `session.invalid_mode` (mode not in the allowlist).
- **Server-side transition record:** the endpoint builds the `ModeTransitionEvent` — `fromMode` = the session's current `CurrentMode`, `toMode` = the request mode, `directionAtTransition` = the session's current direction, `occurredAt` = `IClock.UtcNow`, `clockSource` = server, `triggeredByTurnId?` = null (a between-turns switch) — and calls the existing `SessionStore.RecordModeTransition(sessionId, event)`, then updates `CurrentMode`. A **no-op switch** (mode == current) is allowed (idempotent; no transition recorded, or a recorded same-mode event — see Step-2.5 Q2).

## BACKEND half (commit 1 — `/tdd`)
**Acceptance:**
- [ ] `POST /api/sessions/{id}/mode` validates the mode against the allowlist (reject off-enum → `400 session.invalid_mode`, sanitized `UiError`); unknown session → `404 session.not_found`.
- [ ] On a valid switch: updates `config.CurrentMode` to the request mode AND records a `ModeTransitionEvent` via the existing `RecordModeTransition` (wire the built-but-unwired method) with the server-derived fields above.
- [ ] A turn created AFTER the switch (`POST …/turns`) is stamped with the **new** mode (the 2c fix — pin this end-to-end: create session cascade → POST /mode realtime → POST /turns → the turn's `Mode == realtime`).
- [ ] The recorded transitions appear in the persisted session JSON (`modeTransitions`) — no secret/no raw-audio (invariants hold; it's metadata only).
- [ ] `/preflight` (backend) clean; full `dotnet test` green.

**Files (backend):** `SessionsController.cs` (+ the route), `ISessionService`/`SessionService.cs` (the orchestration), `Sessions/SessionModels.cs` or a request-DTO file (`SetModeRequest`), tests. `SessionStore.RecordModeTransition` already exists — wire it; flag at Step 2.5 if it needs a signature change.

## FRONTEND half (commit 2 — `/tdd` the flow + manual-smoke the wiring)
**Acceptance:**
- [ ] `ModeToggle` calls the new endpoint on a switch via a **DI'd flow** (lesson §7 — `sessionsApi.setMode(sessionId, mode)` + a `*Actions` flow injected with store+api), in addition to the existing store write. Clean-separation holds (the component dispatches an intent; no transport detail in the component).
- [ ] The existing **Flow-G realtime teardown on switch-away** stays (the realtimeConnectionManager teardown — lesson §18); the new POST is additive.
- [ ] On POST failure → sanitized `UiError` to the store (single sink, §2/§7); the switch behavior on failure is the Step-2.5 Q1 decision below.
- [ ] A `SetModeRequest` TS mirror (+ optionally the `ModeTransitionEvent` TS mirror — `{transitionId, fromMode, toMode, directionAtTransition, occurredAt, clockSource, triggeredByTurnId?}`, mirror of `SessionModels.cs`) — needed by H.3 later; graduate it now if cheap.
- [ ] `npm run format:check && lint && typecheck && test` green.

**Files (frontend):** `api/sessionsApi.ts` (+ `setMode`), a `state/*Actions` module or extend `sessionActions`, `components/ModeToggle.tsx` (dispatch the flow), `types/domain.ts` (the mirror), tests.

## Cross-doc invariant impact (orchestrator writes hot at Step 9 / round-seal)
- **New endpoint** → `ARCH-009` route table + **Appendix A** `SetModeRequest` DTO row.
- **`ModeTransitionEvent` graduation** → it's recorded/persisted now (was unwired): an `ARCH-010` / `ARCH-017` Flow-G realization note + (if a TS mirror lands) a `web/CLAUDE.md` cross-doc mirror-registration row. `ModeTransitionEvent` is already in `ARCH-005`/Appendix A — no new record, just newly-wired.
- **`config.CurrentMode` is now mutable mid-session** (was effectively frozen) — a one-line ARCH-009/ARCH-017 realization note.
> Implementer flags these at Step 9 categorized; the orchestrator writes the rows. No safety invariant changes (mode is a non-secret enum; the transition is metadata).

## Things to flag at Step 2.5
1. **(frontend) Optimistic vs await on the toggle.** The whole point of 2c is backend/frontend mode consistency, so a failed POST must not leave them divergent. My vote: **await the POST success before finalizing the store-mode switch** (on failure → surface error + keep the prior mode → no divergence). Alternative: optimistic store update + error-surface (UI snappier but a failed POST re-introduces the 2c divergence). Confirm.
2. **(backend) No-op switch (mode == current).** My vote: **allow + treat as idempotent** (200, no transition recorded) — a redundant toggle shouldn't pollute the timeline. Confirm.
3. **(backend) Block a switch mid-turn?** ModeToggle is already gated (locked during recording/processing/playing). The endpoint could also reject a switch while a turn is in-flight (defense-in-depth). My vote: **the endpoint stays simple (always updates); the frontend gate is the guard** (single-trusted-user; matches the toggle's existing lock). Confirm — or add a backend in-flight guard if cheap.
4. **(frontend) Reuse the `Create` response shape?** The endpoint returns the updated `InterpretationSession`; the frontend can resync `config.currentMode` from it (belt-and-suspenders) or just trust its optimistic value. My vote: **resync from the response** (authoritative). Confirm.

## Dependencies + sequencing
- **Depends on:** the shipped session/turn surface (Phases B/D/E). **Coordinate the two halves** on the shared contract above.
- **Sequencing:** dispatch each half **after** its impl's current smoke-bugfix slice lands (backend after 048; frontend after 049) — or in parallel if an impl has bandwidth (slice-atomicity: never interrupt a mid-slice impl). Backend 2c pairs with 048's area.
- **Blocks/pairs:** fixes the comparison validity (G.5); the timeline data pairs with **H.3** (session-history view).

## Estimated commit count
**2** — backend (endpoint + transition wiring + the create-after-switch end-to-end pin) = commit 1; frontend (toggle call + the DI'd flow + the mirror) = commit 2. Cross-area + a cross-doc invariant change (new endpoint + ModeTransitionEvent graduation) → the two commits stagger; the doc rows ride the orchestrator round-seal. No safety invariant.

## Lessons-logged candidates anticipated
- **Convention candidate** — "A turn's mode is stamped from the session's `CurrentMode` at create → a mid-session mode switch MUST propagate to the backend (a `POST …/mode` endpoint), not just the frontend store, or turns are mislabeled + the by-mode comparison is invalid. Record the switch as a `ModeTransitionEvent` (Flow-G timeline)."
- **Architecture-doc note** — `config.CurrentMode` is mutable mid-session via the new endpoint; `ModeTransitionEvent` is now wired (Flow G) — feeds the H.3 history timeline.

## How to invoke (each half)
1. Read this brief (the shared contract is binding for both halves).
2. **Backend:** `/tdd mode_switch_endpoint` — RED the endpoint (validate/404/400, CurrentMode update, RecordModeTransition wired, create-after-switch mode pin) first.
   **Frontend:** `/tdd mode_switch_toggle_call` — RED the DI'd flow (POST on switch, error→store, clean-separation) first.
3. Step 2.5: answer the Qs (frontend Q1/Q4; backend Q2/Q3). Step 9: categorized summary (esp. the cross-doc rows) + ship/no-ship + draft commit message.
