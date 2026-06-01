# LESSONS.md — AI Interpreter Workbench (the React frontend)

> Full prose for every lesson logged during work in `web/`. The compact index lives in `web/CLAUDE.md` "Lessons logged" table.
>
> **Lesson numbers are stable IDs.** New lessons get the next sequential number. Numbers may be referenced from code comments, commit messages, and cross-references between lessons. **Don't reorder; don't reuse a deleted number's slot.**
>
> **Lessons start at §1.** Each code area has its own lesson sequence — lessons don't carry across code areas.

---

## Lesson format

```markdown
## <a id="N"></a>N. <Short topic> — <one-line rule>

**Date:** YYYY-MM-DD.
**Source slice:** <slice-id or commit hash>.

<2-5 paragraphs explaining: what was discovered, why it matters, how to
apply the rule, what edge cases are still open. Cite file:line references
where applicable.>

**Rule:** <one-sentence summary, same as the heading subtitle>.
```

---

## <a id="1"></a>1. Prettier `--write .` reformats orchestrator-owned area docs — `.prettierignore` them

**Date:** 2026-05-28.
**Source slice:** A.1 (solution + repo scaffold).

During the A.1 web scaffold, running `prettier --write .` across `web/` silently reformatted `web/CLAUDE.md` (padded markdown tables, inserted blank lines) — an orchestrator-territory file the implementer must never touch. It was caught and restored byte-for-byte from the session-start Read, but the root cause is that Prettier's default glob includes markdown and walks the whole area, so any formatter run reaches the area's `CLAUDE.md` / `LESSONS.md`.

Why it matters: `CLAUDE.md` and `LESSONS.md` are orchestrator-owned (root + area `CLAUDE.md` "Implementer must NOT touch"). Silent reformatting by an implementer-run formatter is exactly the territory-drift the staggered-commit model exists to prevent, and it produces noisy diffs on files that should change only via deliberate orchestrator edits.

How to apply: every area that runs a markdown-capable formatter lists its area docs in the formatter's ignore file. For `web/`, `web/.prettierignore` includes `CLAUDE.md` and `LESSONS.md` (added in A.1); any new area doc or new formatter gets the same guard. The mechanism is web-specific: the backend uses `dotnet format`, which targets `.cs` and does not touch markdown, so `server/CLAUDE.md` / `server/LESSONS.md` are not at risk from the backend formatter — but the same rule applies if a markdown formatter is ever added to `server/`.

**Rule:** Add orchestrator-owned area docs (`CLAUDE.md`, `LESSONS.md`) to any markdown-capable formatter's ignore file so an implementer-run formatter can't reformat them.

---

## <a id="2"></a>2. Minimal external store — pure `createSessionStore()` factory + thin `useSyncExternalStore` hook

**Date:** 2026-05-29.
**Source slice:** D.1 (app shell, API clients, state store).

ARCH-007 calls for "a single store/hook … no heavy state library." D.1 realizes it as a **pure factory** `createSessionStore()` returning `{ getState, subscribe, ...actions }` over an immutable `UiSessionState`, plus a thin `useSessionState` hook that wires the factory's `subscribe`/`getState` into React's `useSyncExternalStore`. The split is the whole point: the factory holds all the deterministic logic (initial state, the `sessionStatus`/`turnStatus` transition setters, the wire-DTO→view-model mapping, the error sink) and is **unit-TDD'd in isolation** (no render); the hook is ~3 lines of React glue (manual-smoke exempt).

Why it matters: `useSyncExternalStore` has two referential-stability contracts that, if violated, cause either missed re-renders or an infinite render loop. (1) An action must produce a **new** state object reference, or React won't re-render. (2) `getSnapshot` (here `getState`) must return a **stable** reference when nothing changed, or React re-renders forever. The store satisfies both by treating state as immutable: every action returns `{ ...prev, ...patch }` (new ref), and `getState` returns the same stored reference until the next action (stable). Pin both with a test (D.1 store test 7: listener fires once on a mutating action; `getState()` returns a new ref after a mutation and the same ref across reads with no intervening action).

How to apply: keep the store the **single error sink** (`addError`/`clearErrors`) so every transport/client failure funnels to one place the UI renders from — components never hold their own error state. The D.1 hook returns the whole state (no selector) deliberately; a selector variant is deferred to D.6 when panels need granular subscriptions, because a naive selector re-introduces the `getSnapshot`-stability footgun (a selector that returns a fresh object each call loops). When that lands, memoize the selection or use `useSyncExternalStoreWithSelector`.

**Rule:** A minimal React store is a pure `createSessionStore()` factory (unit-tested, immutable state — new ref per action, stable ref between actions) + a thin `useSyncExternalStore` hook (manual-smoke); the store is the single error sink, and any future selector variant must preserve `getSnapshot` referential stability.

---

## <a id="3"></a>3. Base-URL-agnostic API clients + one `http` failure boundary that always yields `UiError`

**Date:** 2026-05-29.
**Source slice:** D.1 (app shell, API clients, state store).

The D.1 clients read `import.meta.env.VITE_API_BASE_URL ?? ''` **per call** (not at module-eval) and prefix every path with it. With base `''` the URLs are relative — the Vite dev proxy (`/api`, `ws:true`) handles them zero-config; with a full base they hit the backend directly (CORS/Origin already configured server-side, C.4b). The client code is **identical** for both modes — only `vite.config.ts` / `.env` differ (ARCH-029). Reading per-call (vs caching at import) is also what makes the base test-stubbable (`vi.stubEnv`).

Why it matters: a frontend that scatters `fetch` calls with ad-hoc error handling leaks raw failure detail into the UI and handles the failure modes inconsistently. The `http.request<T>()` helper is the **single failure boundary**, and it is only complete if it covers ALL THREE ways a request can fail: (1) a **non-OK `Response`** → parse a real `UiError`-shaped body (B.9 routed-path errors) through, else synthesize `http.<status>`; (2) a **`fetch` rejection** (network error / backend-down `TypeError`) → synthesize `network.error` (`retryable:true`); (3) an **unparseable / empty 2xx body** → synthesize `response.invalid` (`retryable:false`). Path (3) was the code-quality reviewer catch in D.1 — the success path's `response.json()` was unguarded, so a non-JSON 2xx threw a raw `SyntaxError` that escaped the boundary; guarding it (RED test → guard → GREEN) is what makes "single boundary" actually true. In every case the raw body / `TypeError` / `SyntaxError` is **never** copied into `safeMessage` (no-leak, mirrors the backend safe-by-construction posture, server lessons §13/§14). Pin the no-leak with a planted-secret assertion (D.1 http test: a ProblemDetails body carrying a secret-looking string → the string appears in neither `safeMessage` nor the serialized `uiError`).

How to apply: the fetch-rejection branch is load-bearing, not optional — the App's on-mount `configApi.getConfig()` bootstrap is the most likely thing to fail in dev (frontend up before backend), and its `catch → store.addError(e.uiError)` contract requires the helper to ALWAYS throw `ApiError` (a raw `TypeError` → `e.uiError` undefined → `addError(undefined)` corrupts the store). The FormData (multipart) path must NOT set `Content-Type: application/json` — let `fetch` set the `multipart/form-data` boundary itself (pin with a negative content-type assertion).

**Rule:** Clients read `VITE_API_BASE_URL ?? ''` per-call (proxy + direct share identical code); the `http` helper is the single failure boundary — non-OK status, fetch-rejection, AND an unparseable 2xx body (`response.invalid`) all map to a typed `ApiError(UiError)`, never leaking a raw body / `TypeError` / `SyntaxError`; the multipart path lets `fetch` set the boundary.

---

## <a id="4"></a>4. Vitest fetch-mock mechanics — `Response` body is single-read; `expect.anything()` ≠ `undefined`

**Date:** 2026-05-29.
**Source slice:** D.1 (app shell, API clients, state store).

Two test-mechanics gotchas surfaced as the only two GREEN-stage failures in D.1 (contracts were correct; the *tests* were wrong). (1) A `Response` body is a **single-use stream** — `vi.fn().mockResolvedValue(oneResponseInstance)` returns the SAME instance to every call, so any test that issues ≥2 requests gets a consumed/locked body on the 2nd read and throws. Use `mockImplementation(() => makeResponse())` to mint a fresh `Response` per call. (2) `expect.anything()` is documented as "matches anything but `null`/`undefined`" — so asserting `fetch` was called with `(url, expect.anything())` **fails** for a client that calls `fetch(url)` with no `init` arg (the 2nd arg is `undefined`). Assert the URL argument directly (e.g. `expect(fetchMock).toHaveBeenCalledWith('/api/config')` or inspect `fetchMock.mock.calls[0][0]`) instead of matching a possibly-absent `init`.

Why it matters: both failures masquerade as contract bugs ("my client is double-fetching" / "my GET isn't passing options") and can send you editing correct production code. Recognizing them as mock-harness artifacts saves the misdirection.

How to apply: default to `mockImplementation(() => freshResponse())` for any fetch mock used by more than one assertion path; assert request URLs/bodies by direct argument inspection rather than leaning on `expect.anything()` for optional args.

**Rule:** In Vitest fetch mocks, mint a fresh `Response` per call (`mockImplementation`, not `mockResolvedValue`) because a body is single-read, and assert request args directly — `expect.anything()` does not match an absent (`undefined`) `init` arg.

---

## <a id="5"></a>5. Config-gating is a pure selector over `providerHealth` — components stay thin

**Date:** 2026-05-29.
**Source slice:** D.2 (session setup, mode + model selectors, config-gating).

Flow A (ARCH-017) — "the SPA disables unconfigured modes" — is realized as **pure selectors** over the store's `providerHealth` (`ConfigResponse`), not as logic embedded in components: `modeAvailability(config)` (cascade requires all three stages STT+translation+TTS; `undefined` config → nothing enabled), `availableModels(config)`, and `canToggleMode(turnStatus)`. They live in `web/src/state/selectors.ts` and are unit-TDD'd in isolation; `SessionSetup`/`ModeToggle` just render the selector results + dispatch intents (clean separation, ARCH-007).

A subtlety worth pinning: **`availableModels` reads the `ConfigResponse` model catalogs straight through and is NOT gated by `configured`.** The backend (`ConfigService.GetConfig`) always populates the catalogs (`realtime.models` / `cascade.translation.models`) regardless of key-presence — `configured` gates the MODE, not the model list. Gating the model list on `configured` would empty the selectors whenever a key is absent, but `CreateSessionRequest` requires both `realtimeModel` + `translationModel`, so an empty selector breaks session-create. Verify the backend's actual population behavior before deciding what a selector returns. (The model selectors constraining to the catalog is also the client-side mitigation of the B.9c-i model-allowlist follow-up — though a runtime membership guard on the onChange cast + the server-side allowlist remain hardening items.)

**Rule:** Realize config-gating as pure selectors over `providerHealth` (unit-tested in isolation; components render the result + dispatch intents); read model catalogs straight from `ConfigResponse` (the backend always populates them — `configured` gates the mode, not the model list).

---

## <a id="6"></a>6. A merging state action must GUARD its lifecycle transition — a merge must never drag a later state backwards

**Date:** 2026-05-29.
**Source slice:** D.2 (session setup) — code-quality reviewer HIGH, fixed in-slice.

`updateSessionConfig(patch)` is the one merging config-mutation action (it subsumed D.1's `configureSession`). Its first implementation set `sessionStatus: 'configured'` **unconditionally** on every merge. That looked fine until you notice `ModeToggle` is reachable **during an active session** (it's gated only by `turnStatus` via `canToggleMode`, enabled between turns for the Flow-G mode switch). So a between-turns mode switch would call `updateSessionConfig({mode})` → reset a live `'active'` session back to `'configured'`, hiding the End button and corrupting the lifecycle (ARCH-007 inv. 9 — a mode switch between turns must keep the session active).

The fix: the transition is **guarded** — `sessionStatus: state.sessionStatus === 'idle' ? 'configured' : state.sessionStatus`. A merge promotes `idle → configured` but never drags a started session backwards. Pin it with a test that mutates an *active* session and asserts the status is unchanged (`does NOT drag an active session back to configured`).

Why it matters: a "merge a partial patch" action is invoked from many UI moments across a session's life; an unconditional status side-effect inside it silently regresses the state machine the first time a later-lifecycle caller appears. The discovery only happened because `ModeToggle`'s active-session reachability was traced — so when an action carries a status side-effect, enumerate every caller's lifecycle context, not just the first one.

**Rule:** When a merging/partial-update action also sets a lifecycle status, guard the transition (`from === X ? Y : current`) so the merge can never drag a later state backwards — and test it from a later-lifecycle state, not just the initial one.

---

## <a id="7"></a>7. Side-effecting flows extract to a DI'd actions module, tested against the real store + a mocked api

**Date:** 2026-05-29.
**Source slice:** D.2 (session setup).

The Start/End flows carry real orchestration (Start: `clearErrors → setSessionStatus('starting') → sessionsApi.createSession → sessionStarted` / on `ApiError` `addError` + revert to `configured`; End: `endSession → sessionEnded` + surface a `persistenceWarning`, or on `ApiError` `addError` + stay `active`). Rather than bury this in a component's onClick (only render-testable, D.7), it lives in `web/src/state/sessionActions.ts` as `startSession(deps)`/`endSession(deps)` taking the store + api as injected deps; `SessionSetup` is a thin caller. `startSession` reads the **store-derived** `CreateSessionRequest` (the form writes live to the store via `updateSessionConfig`; Start reads `store.getState()`) — consistent with the store-is-source-of-truth posture.

These are TDD'd against the **real `createSessionStore()`** (already unit-tested) + `vi.spyOn` for invocation order + real-state assertions, with only the api mocked — higher fidelity than a hand-rolled mock store, no mock drift. Pin both error arms (`ApiError` AND a non-`ApiError` fallback → a fixed-message `UiError`, asserting no raw-error leak — mirrors lesson §3's no-leak boundary), invocation ordering (e.g. `clearErrors` before `createSession`), and guards (no-op on a null `sessionId`) — all without a render. The clean (no-warning) and warning paths are both pinned so we don't add a phantom error on success.

**Rule:** Extract side-effecting flows to a DI'd `*Actions` module (store + api injected), keep the component a thin caller, and TDD it against the real store + a mocked api — pinning both error arms (incl. the non-typed-error fallback + no-leak), ordering, and guards without a render.

---

## <a id="8"></a>8. Audio capture — a manual-smoke browser shell over pure test-first helpers; the two AudioWorklet gotchas

**Date:** 2026-05-29.
**Source slice:** D.3 (audio capture controller) — incl. a code-quality reviewer HIGH + a security MEDIUM, both fixed in-slice.

The capture controller is the canonical "browser-API shell" case (ARCH-020 exempt): `getUserMedia`/`AudioContext`/`AudioWorkletNode`/`MediaRecorder` wiring is **manual-smoke** (`audioCaptureController.ts`, `pcmWorklet.ts`), while the deterministic cores are **extracted to pure modules and TDD'd** — `floatTo16BitPCM` (linear16 clamp + asymmetric int16 scale: `-1 → -32768`, `+1 → 32767`, no wraparound — a wrap corrupts audio to Deepgram; pin a mid-range case like `0.5 → 16383` to distinguish the asymmetric formula from a symmetric one), `probeRecorderMimeType` (the ARCH-030 order; Safari<18.4 = mp4 not webm), `micErrorToUiError` (fixed message, no raw-`.message` echo — lesson §3 no-leak), and `clampBlobDurationMs` (the security MEDIUM: bound any client-initiated record/timer — cap 60s, default non-finite/≤0 to a sane value — so an unbounded `setTimeout` can't exhaust resources). The worklet **imports** the pure conversion, so the corruption-prone math is unit-tested *outside* the un-testable worklet realm.

Two AudioWorklet gotchas, both load-bearing:
1. **A capture worklet MUST `node.connect(context.destination)`** (with silent output) or the audio graph may never pull `process()` — so **no frames ever flow** (the silent feature-broken HIGH). The node having no "real" output doesn't matter; the connection is what drives the processing.
2. **Type the worklet-realm globals** (`AudioWorkletProcessor`, `registerProcessor`, `sampleRate`) with an **inline ambient `declare`** in the worklet module rather than pulling `@types/audioworklet` for ~3 symbols (the worklet file is a module — it imports `pcm.ts` — so module-scoped declares satisfy tsc there).

Also: the controller emits frames/errors via **callbacks only** (`onFrame`/`onError`) and imports neither the store nor any transport client — clean separation (ARCH-007), and it keeps capture reusable by the realtime path. Teardown on any setup failure (release the stream + context, `onError`, return null) so a rejected `addModule` / a `MediaRecorder` error can't leak a live `AudioContext` or hang forever.

**Rule:** Capture is a manual-smoke browser shell over pure test-first helpers (conversion / probe / mic-map / duration-clamp); a capture AudioWorklet MUST connect to `destination` (silent) or `process()` never fires, and type its realm globals with an inline ambient `declare` (no `@types` dep); the controller emits via callbacks (no store/transport import) and tears down on any setup failure.

---

## <a id="9"></a>9. The cascade WS client — fake-`WebSocket` TDD, a pure dispatch router, audio off the store, and lifecycle hardening

**Date:** 2026-05-29.
**Source slice:** D.4a (cascade streaming client) — incl. 2 code-quality HIGHs + a security MEDIUM, all fixed in-slice.

`cascadeStreamClient` is the frontend cascade transport. Like the backend WS endpoint, it's TDD'd against an **injected fake `WebSocket`** (`wsFactory?` defaulting to the global) — so the full lifecycle (open → send the `start` frame on `onopen` → forward binary frames → `stop` → dispatch inbound → `done`/close) is unit-tested, not just the pure helpers. That coverage is what catches the orchestration bugs a pure-helper-only test misses (see the HIGHs below). The pure core is `dispatchCascadeMessage(rawText, {store, onAudio})` — a router mapping each server message to a store action: `transcript→appendTranscriptSegment`, `latency→appendLatencyEvent`, `cost→setTurnCost`, `error→failTurn` (projecting `ProviderError→UiError`, dropping `provider`/`httpStatusCode`, mirroring B.8), `done→completeTurn`. The `audio` frame routes to an **`onAudio` callback ONLY — never a store action** (invariant-#3 discipline on the client: raw audio never enters the store/`UiSessionState`; playback (D.5) consumes the callback).

Three lifecycle-hardening rules, each a review-surfaced bug:
1. **A server `error` frame is terminal** (like `done`) — otherwise the `terminal` flag stays false and the server's subsequent socket close fires a spurious second failure (`cascade.connection_lost`). Set `terminal` on `done` AND on the first failure (error frame or abnormal close), and only fail-on-close when not yet terminal. (HIGH)
2. **Tear down the prior socket on re-`start()`** (null its handlers + close) — otherwise turn-1's `onclose`/`onmessage` stay live and fire a spurious failure on turn-2's socket. (HIGH)
3. **Guard the dispatch switch BODY, not just `JSON.parse`** — a known-type frame missing a sub-field (e.g. `{type:'error'}` with no `error`) throws past the parse guard, escapes `onmessage`, and stalls the turn. Wrap parse + routing in one try/catch → ignore. (security MEDIUM) Also: `toWebSocketUrl` must be trailing-slash-safe; a stale/mismatched-`turnId` `done` is ignored (don't strand `currentTurn`).

**Rule:** TDD a WS transport client against an injected fake `WebSocket` (full lifecycle, not just helpers); route inbound via a pure dispatch fn (audio → a callback, never the store — invariant #3); and harden the lifecycle — a server `error` is terminal, re-start tears down the prior socket, and the dispatch BODY (not just `JSON.parse`) is guarded.

---

## <a id="10"></a>10. Normalize streaming transcripts IN the store so the panel is dumb

**Date:** 2026-05-29.
**Source slice:** D.4a (cascade streaming client).

The cascade WS streams `TranscriptSegment`s — interleaved partials (`isFinal:false`) and finals (`isFinal:true`), per role (source/target). Rather than store every segment and make the panel reconstruct "the current partial + the finalized history," the store's `appendTranscriptSegment` **normalizes**: a partial **replaces** the trailing non-final entry (or appends one if none); a final **finalizes** the trailing entry (replace + mark `isFinal:true`); a partial arriving **after** a final starts a NEW running entry. Segments route to `sourceTranscript`/`targetTranscript` by `role`. The result is a clean `{text,isFinal}[]` the panel (D.6) renders directly — "partials as they arrive, replaced by finals" (ARCH-011) — with no partial-tracking logic in the component.

**Rule:** Normalize streaming transcript partials/finals in the store action (replace trailing partial / finalize on `isFinal` / new entry after a final; route by role) so the rendering component stays a dumb projection of `{text,isFinal}[]`.

---

## <a id="11"></a>11. Recording orchestration — a DI'd controller sequencing createTurn→store→capture→WS, and its three concurrency edges

**Date:** 2026-05-29.
**Source slice:** D.4b (recording controls + cascade wiring) — incl. a code-quality HIGH + a concurrent-start race, both fixed in-slice.

The cascade recording turn is wired by a DI'd `createRecordingController(deps)` (`recordingActions.ts`, same pattern as `sessionActions` §7): `startRecording` sequences `createTurn(sessionId)` → `beginTurn` → `capture.startStreaming` (piping `onFrame → client.sendFrame`, `onError → store.failTurn`) → `client.start({…, sampleRate from the capture handle, ttsVoice:'' so the backend ResolveVoice picks by target language})`. `RecordingControls` is a thin store/selectors projection (Start/Stop gated by `canStartRecording`/`canStopRecording` — the ARCH-007 transition table); the orchestration owns all the wiring. Tested against the real store + mocked api/client/capture (call-order via `invocationCallOrder`), no render.

Three concurrency edges, each a real bug:
1. **Pre-open frame-queue** (in the client): capture's `sampleRate` is known synchronously, so `client.start()` runs right after `startStreaming` — but the socket opens async (the `start` frame is sent on `onopen`). Frames captured in the CONNECTING window must be **queued and flushed after the start frame**, never `send`-ed on a CONNECTING socket (throws) and never dropped. An internal `isOpen` boolean is cleaner than `readyState` coupling.
2. **Stop-defer + idempotency** (the symmetric HIGH): `stop()` on a still-CONNECTING socket throws `InvalidStateError` (Stop right after Start). Defer the stop frame to `onopen` if connecting, and make `stop()` idempotent (a `stopped` flag → at most one stop frame) — the mirror of the deferred start frame.
3. **`inFlight` concurrent-start guard** (in the orchestration): a double-click on Start fires two `createTurn`s — the second `beginTurn` stomps the first and orphans a server turn. An `inFlight` flag set before `createTurn` and cleared in `finally` blocks the second. (The UI gate `canStartRecording` is not enough — the async window between click and state update is the race.)

Also: `createTurn` precedes the WS `start` (the start frame needs the backend `turnId`); a `createTurn` failure → `addError` (`ApiError.uiError` or a fixed `turn.create_failed`) + abort before any capture/WS resource exists (no orphan).

**Rule:** Wire a recording turn via a DI'd controller (createTurn→store→capture→WS, tested against the real store + mocked deps); resolve the capture-starts-before-socket-open races in the client (pre-open frame-queue + deferred/idempotent stop) and guard concurrent starts with an `inFlight` flag (the UI gate can't cover the async window).

---

## <a id="12"></a>12. Cascade TTS playback — MSE-primary shell over pure helpers; the blob fallback can't progressively play a live stream

**Date:** 2026-05-29.
**Source slice:** D.5 (playback controller) — incl. 2 code-quality HIGHs in the fallback + a security MEDIUM, all fixed in-slice.

`playbackController` plays the streamed cascade TTS audio. Like capture (§8), it's a manual-smoke browser shell (`MediaSource`/`SourceBuffer`/`HTMLAudioElement`) over **pure, test-first helpers**: `decodeBase64Audio` (the `audio`-frame bytes), a no-overlap guard (a new turn resets the prior — single active playback), the once-per-turn `playback.started` stamper (stamped on the `playing` event → `store.appendLatencyEvent`, not on chunk arrival), and `clampAudioContentType` (an allowlist + params-stripped clamp on the server-supplied content-type — defense-in-depth at the client data boundary, mirroring backend C.4b/ARCH-019). The `onAudio` no-op the cascade client shipped (D.4a/D.4b) is closed by a **settable delegate** on the client singleton (`setAudioSink(fn)`, wired at the `main.tsx` composition root) — so playback attaches without reconstructing the client.

Two playback truths worth pinning:
1. **MSE is the primary path; the blob fallback can't progressively play a live stream.** An `HTMLAudioElement` `src` (object URL of an assembled `Blob`) is *static* — you can't append to it mid-playback. So the blob fallback is best-effort (re-assemble + play when idle; a true completion needs a `done`-driven `flush()`). The MSE path (append queue pumped on `updateend`) is what streams. Two HIGHs lived here: re-`switchToBlob` per chunk restarted the element from 0 (no audio); `play()` on an empty `src` when MSE is unsupported. Keep `play()` on the MSE path; in blob mode (re)assemble+play only when idle.
2. **Raw audio is transient, never in the store** (invariant-#3 discipline on the client): the controller holds bytes only to feed the element; only the `playback.started` *latency marker* reaches the store. `reset()` clears the buffers AND revokes the object URL (no leak).

> Note (cascade limitation): `playback.started` is a **frontend display marker** — the cascade WS has no client→server latency channel, so it never reaches the persisted turn (realtime reports via `POST …/events`). The persisted/aggregated cascade summary shows speechEnd→playback `n/a`. And `relativeMs` on the client-stamped marker is a placeholder (`0`) — its real value is its absolute browser `timestamp`; a consumer computing speechEnd→playback must use the timestamp delta, never the placeholder (streaming-honesty).

**Rule:** Playback is a manual-smoke MSE/`HTMLAudioElement` shell over pure helpers (decode / no-overlap / once-`playback.started` / content-type clamp); MSE is primary (append on `updateend`), the blob fallback is best-effort (a static element `src` can't progressively play a live stream); audio is held transiently (never in the store, revoke the URL on reset); wire the client's `onAudio` via a settable delegate at the composition root.

---

## <a id="13"></a>13. Cascade metrics — three sources; the frontend computes the top-level deltas the backend structurally can't

**Date:** 2026-05-29.
**Source slice:** D.6 (transcript/metrics/cost panels).

Cascade latency display draws from **three distinct sources — do not conflate them**:
1. **Per-stage latency (stt/translation/tts) → from the store.** The cascade WS streams `latency` events; the store keeps `currentTurn.latency.stages[name] = relativeMs` (server-computed). `deriveTurnMetrics` **passes these through** — it does NOT recompute them (lesson §7: `relativeMs` is per-event display; the backend owns that math).
2. **Top-level deltas (speechEnd→firstAudio/playback, totalTurn) → frontend-computed, because the backend STRUCTURALLY CANNOT for cascade.** They need the client-side events `turn.recording.started`/`stopped`/`completed` + `playback.started`, which never reach the backend (the cascade WS has no client→server latency channel; the turn is persisted on `done`). So the frontend stamps those (browser clock) and computes the deltas itself, mirroring the backend `MetricsAggregator.Between` — **absolute-`Timestamp` subtraction, NEVER `relativeMs`**, an absent endpoint → `null`/`n/a`, cross-clock negatives **disclosed, not clamped** (ARCH-013). `speechEnd→playback` is browser-clean (both client stamps); `speechEnd→firstAudio` mixes browser-stopped + server-firstAudio (cross-clock — accurate on localhost where the clocks match). This is NOT re-implementing the aggregator redundantly — for cascade client-timing it's the ONLY source. (Realtime (E) reports client events via `POST …/events`, so the backend computes realtime top-level canonically — read the summary there.)
3. **Session averages → from the backend `GET /api/sessions/{id}/summary` (canonical).** The MetricsPanel reads `ModeSummary` for the by-mode averages; cascade's `AvgSpeechEnd*` is `n/a` (per #2's persistence gap), the per-stage avgs are present.

To make #2 reachable, the store had to **retain what D.4a dropped**: `appendLatencyEvent` keeps the raw `latencyEvents[]` (with absolute timestamps — `stages` alone is insufficient), and `setTurnCost` keeps the full `CostEstimate` (the CostPanel's model + assumptions tooltip). `completeTurn` stamps `turn.completed` (browser, before finalize) so the totalTurn terminal is present + browser-clean (the WS doesn't stream it); `tts.complete` (server) is the documented cross-clock fallback. (Footnote: `deriveTurnMetrics`'s by-name de-dup is FIRST-wins, mirroring the backend's first-arrival; the store's `stages` map is LAST-wins — benign, since the server stamps each event name once per turn.)

**Rule:** Cascade metrics = per-stage from the store (passed through, never recomputed) + top-level client-timing deltas frontend-computed (`Between` on absolute timestamps, the ONLY source since the backend can't for cascade) + session-averages from the backend `GET /summary`. Retain the raw `latencyEvents[]` + full `cost` in the store; stamp the client lifecycle markers (recording.started/stopped/completed) browser-clock; cross-clock disclosed, never clamped.

---

## <a id="14"></a>14. jsdom/Testing-Library component tests — per-file env to keep the node suite untouched; `errorCopy` is the single never-raw map

**Date:** 2026-05-29.
**Source slice:** D.7 (error banner + the 2 PRD component tests) — closes Phase D.

The project's first component-render tests stand up jsdom + Testing-Library **without disturbing the existing node-env unit suite** (the clients/store/selectors tests rely on node's `fetch`/`FormData`/`Blob` globals; a global `test.environment: 'jsdom'` switch risks changing their fetch/Response behavior). The pattern: keep `vite.config.ts` `test.environment: 'node'` as the default; mark ONLY the component test files with **`// @vitest-environment jsdom`** (per-file); a `setupFiles` that does only `import '@testing-library/jest-dom/vitest'` (registers matchers — DOM-free at import, safe in the node files); `afterEach(cleanup)` inside each jsdom file (node files never import `@testing-library/react`). Mock `navigator.mediaDevices.getUserMedia` via **`vi.stubGlobal`** (symmetric teardown via `unstubAllGlobals`), not `Object.defineProperty`. Reuse lesson §4 (a `Response` body is single-read → `mockImplementation`, not `mockResolvedValue`) for any fetch stub the render path hits.

`ErrorBanner` renders the store's `UiError[]` via **`errorCopy(error) → string`**, a pure code→actionable-copy map that reads ONLY `error.code` and returns fixed copy — it **never echoes `safeMessage`** (structural never-raw guarantee, ARCH-007/018; pinned by a planted-`RAW-PROVIDER-LEAK` sentinel that must be absent for both mapped and unmapped codes). It supersedes any inline raw-`safeMessage` rendering. An unmapped code → a safe generic ("Something went wrong. Please retry."), never blank/raw.

Spec note (carried into ARCH-020 / MVP_TASKS D.7 at this round): mic-denied → the turn **fails** (Stop disabled), and **Start is RE-ENABLED for retry** (matching the "Enable mic access and retry" copy + the D.2 retry-after-fail design) — NOT "Start disabled" (which would trap the user, there being no dismiss). The component test asserts that truthful settled state.

**Rule:** Stand up jsdom/Testing-Library with per-file `// @vitest-environment jsdom` (node default preserved) + a matcher-only `setupFiles` + per-file `cleanup`; mock browser globals via `vi.stubGlobal`; render error copy through a single pure `errorCopy(code)` map that never echoes `safeMessage` (sentinel-pinned).

---

## <a id="15"></a>15. Realtime WebRTC transport — manual-smoke `RTCPeerConnection` shell over pure handshake seams + a stateless GA-event normalizer; the `ek_` is a transient local

**Date:** 2026-05-30.
**Source slice:** E.3 (browser WebRTC transport library) — opens the Realtime frontend.

The Realtime path's first frontend slice is the WebRTC analogue of the cascade transport (§9) + playback (§12) pattern: the messy browser-internal orchestration is a **manual-smoke shell**; everything deterministic is pulled into pure, unit-TDD'd seams. Three pieces:

1. **Mint client** (`realtimeApi.mintClientSecret`) — `POST /api/realtime/client-secret` over the shared `http` `request` boundary (§3); our-backend-JSON-only. Returns `{clientSecret:'ek_…', expiresAt, model}`; `model` is **omitted** from the request body when absent (the backend resolves the default — not `model: undefined`).

2. **Pure GA-event normalizer** (`realtimeEvents`) — `parseRealtimeEvent` (JSON-parse + non-object guard → `null`, never throws) + `normalizeRealtimeEvent` → a discriminated `NormalizedRealtimeEvent` union (`audioDelta` / `targetTranscriptDelta` / `sourceTranscriptDelta` / `sourceTranscriptCompleted` / `responseCreated` / `responseDone` / `error`). **Stateless — classify + extract payload only**; the stateful first-of-type latency stamping + browser-clock `LatencyEvent` construction + store dispatch is E.4's job (mirroring how the cascade pure router §9 sat apart from the store actions §10). GA field reads (smoke-pending): deltas read `delta`, `…input_audio_transcription.completed` reads `transcript`, an error reads the nested `error.code` (the raw `error.message` is **NEVER** echoed — classification only; E.5 builds the safe message). Accepts the legacy `response.audio.delta` alias for `response.output_audio.delta`; `response.done` is the target-transcript-final signal (the source finalizes on its own `.completed`); unknown/malformed → `null`.

3. **WebRTC handshake** (`realtimeWebRtcClient`) — the `createRealtimeWebRtcClient` factory (`RTCPeerConnection` + `oai-events` data channel + `getUserMedia`/`addTrack` + `createOffer`/`setLocalDescription` + SDP exchange + `setRemoteDescription`) is a **manual-smoke shell**: browser WebRTC is root-posture exempt (unlike WebSocket, which cascade fake-tested at §9 — `RTCPeerConnection`'s SDP/ICE/track/DC surface re-implements the browser if faked). The deterministic seams ARE pinned: `realtimeCallsUrl === 'https://api.openai.com/v1/realtime/calls'` with **no `?model=`** (appending it → HTTP 400 — the bug only surfaces at live smoke otherwise); `exchangeSdpOffer(offer, ek)` POSTs the **raw SDP** with `Authorization: Bearer <ek>` + `Content-Type: application/sdp`, returns the answer as **text** (not the JSON `request` boundary), and surfaces a non-OK status (**status-derived `retryable`**: 4xx→false, 5xx/429→true) / a fetch-rejection (→`retryable:true`) as a typed sanitized `ApiError` (`realtime.connect`) — the raw body is never read (mirrors §3). The capture surface is the realtime client's own `getUserMedia({audio:true})` + `addTrack` of the raw track (NOT the cascade controller's linear16/worklet frames — irrelevant to WebRTC; discharges the D.3 capture-reuse note).

**SAFETY (invariant #2):** the `ek_…` enters `web/` for the first time here. It lives as a **stack-local in the WebRTC client**, used only as the SDP-exchange Bearer — never written to `sessionStore`/persisted/logged, never returned to the store layer. The frontend has no persistence path (E.1's backend sentinel pins #2 on the wire), so there is no frontend sentinel to add at E.3; when E.4/E.5 wire realtime into the store, that slice asserts the `ek_` is absent from the store shape.

**Foundation slice:** E.3's exports are consumer-pending — E.4 wires manual turn control + event mapping + metrics, E.5 the connection lifecycle (the persistent pc + idempotent teardown-before-reconnect — the §11 `inFlight`/teardown pattern guards the double-`connect()` orphan-leak, deferred to E.5).

**Rule:** Realtime WebRTC transport = a manual-smoke `RTCPeerConnection`/`oai-events` shell over pure, fetch-mock'd/unit-TDD'd seams — the GA handshake contract (calls URL **no `?model=`**, Bearer `ek_`, `application/sdp`, **text** answer; status-derived `retryable`) + a pure stateless GA-event normalizer (classify-only; E.4 owns the stateful stamping); the `ek_` stays a transient client local, never the store.

---

## <a id="16"></a>16. Realtime data path — a per-turn stateful event sink: incremental-delta accumulation, `audioDelta` timing-only (#3), `responseDone`-finalize, report-to-backend

**Date:** 2026-05-30.
**Source slice:** E.4a (realtime event sink + turn-event reporting) — the realtime data path (split from E.4; E.4b is turn control + UI).

E.4a wires E.3's pure normalizer (§15) into the store + backend — the realtime analogue of the cascade store-normalizing action (§10), but as a **per-turn stateful sink** (`createRealtimeEventSink({ store, clock })`), because realtime needs per-turn first-of-type + per-role accumulation state. Four load-bearing truths:

1. **Realtime transcript deltas are INCREMENTAL tokens — accumulate before handing to the store.** The store's §10 `appendTranscriptSegment` was built for cascade, where each partial is the **cumulative** full-so-far hypothesis (a later partial REPLACES the trailing one). Realtime `…transcript.delta` events are **incremental** (`'ho'` then `'la'` = `'hola'`). So the sink **accumulates per-role running text** and passes the **cumulative** string to the store (which replaces the trailing partial with the growing text). Passing raw deltas would drop earlier tokens (only `'la'` renders). The accumulation lives in the sink — **`sessionStore.ts` stays cascade-shaped, untouched**. `sourceTranscriptCompleted` carries the authoritative full transcript → replace the running source partial with it (`isFinal:true`) AND **reset the source accumulator** (a late stray source delta must not concatenate onto stale text — a real HIGH caught in review). `responseDone` is the **target-final signal** (there is no target-`.completed` event): finalize the running target partial (accumulated text, `isFinal:true`) before `completeTurn` — guard empty (no target deltas → no empty final segment).

2. **`audioDelta` is TIMING-ONLY (invariant #3).** Unlike cascade (base64 audio over the WS → playback callback, §12), realtime audio arrives as the **WebRTC media track** (E.3 `pc.ontrack` → `<audio>`). The DC `response.output_audio.delta` event is used only to stamp `realtime.first_audio_delta` (first-of-type) — the sink **never reads `event.base64`** and writes **no audio** to the store. Structurally enforced: the sink takes a `Pick<SessionStore>` with **no audio sink**, so it CAN'T persist audio (the realtime analogue of the cascade no-audio sentinel, §9). _(Smoke-pending: whether WebRTC emits `output_audio.delta` on the DC at all — it may be WS-transport-only; if absent, `first_audio_delta` stays honest `n/a` and `playback.started` (E.4b/E.5, the `<audio>` playing event) is the first-audio timing.)_

3. **First-of-type stamps fire once per turn; the store owns `turn.completed`.** Per-turn booleans gate `realtime.first_audio_delta`/`realtime.first_transcript_delta` (a **fresh sink per turn** — no `reset()` foot-gun). On `responseDone` the sink calls `completeTurn(turnId,'completed')` ONLY — the store's `completeTurn` (D.6) already stamps `turn.completed` browser-clock + finalizes; a separate stamp would double it. `turnId` is read from `currentTurn` (keeps the `{store, clock}` factory signature).

4. **Realtime reports its client events to the backend; the backend aggregates the canonical metrics.** `sessionsApi.appendTurnEvents(sid, tid, events)` → `POST /api/sessions/{id}/turns/{turnId}/events` (body `{ events }`, the backend `AppendEventsRequest`). This is the realtime half of the §13 three-source model: cascade structurally can't report client timing (no client→server channel) so the frontend computes its own top-level deltas; realtime CAN (it POSTs the events), so the **backend** computes the canonical realtime top-level metrics and the frontend reads `GET /summary`. The metrics split: E.4a stamps the event-derived markers (`first_audio_delta`/`first_transcript_delta`); `recording.started/stopped` come from E.4b's turn control; `session.connecting/connected` from E.5's connection; the backend aggregates all reported events.

**Rule:** Realtime data path = a per-turn stateful sink (`{ store, clock }`) mapping `NormalizedRealtimeEvent`→store: **accumulate** incremental deltas into cumulative partials (the §10 store action is cascade-cumulative; reset the source accumulator on `.completed`; finalize the target on `responseDone`), stamp first-of-type latency once each browser-clock, `audioDelta` is **timing-only** (never store audio — #3, enforced by an audio-sink-less `Pick<SessionStore>`), let the store own `turn.completed`, and report client events via `appendTurnEvents` so the backend aggregates the canonical realtime metrics.

---

## <a id="17"></a>17. Realtime turn control — a DI'd controller (the §11 analogue): buffer-delimited manual turns, normalize-into-controller, interim audio

**Date:** 2026-05-30.
**Source slice:** E.4b (realtime turn control + RecordingControls wiring + TranscriptPanel) — **closes the realtime reachability gap** (the path is now reachable + audible from the real UI). Completes E.4.

E.4b wires the realtime turn into the real UI — the direct analogue of the cascade recording controller (§11). A DI'd `createRealtimeTurnController({ store, client, api, clock })` sequences a manual VAD-off turn:

- **Start** (`startTurn`): `api.createTurn` → `store.beginTurn({ mode:'realtime', direction })` → **lazy-connect** the E.3 client if not connected (E.5 hoists to session-start) → create a per-turn E.4a sink → wire `client.onServerEvent = raw => sink.handle(normalizeRealtimeEvent(parseRealtimeEvent(raw)))` → `sendClientEvent({ type:'session.update', session:{ audio:{ input:{ turn_detection:null }}}})` → `sendClientEvent({ type:'input_audio_buffer.clear' })` → stamp `turn.recording.started` (browser clock). An `inFlight` guard makes a concurrent `startTurn` a no-op (§11).
- **Stop** (`stopTurn`): `sendClientEvent({ type:'input_audio_buffer.commit' })` → `sendClientEvent({ type:'response.create' })` → stamp `turn.recording.stopped`. Guards on an active `currentTurn`.
- On the sink's `responseDone` → `api.appendTurnEvents` reports the turn's client events (E.4a).

**Buffer-delimited turns:** with `turn_detection:null` (manual VAD off) the mic track streams **continuously** (E.3 `addTrack`) and the server buffers all input until `commit` — there is **NO per-turn mic start/stop**; the turn is delimited by `clear` (Start) and `commit`+`response.create` (Stop).

Three refinements worth pinning:

1. **The client refactor pushed `normalize` INTO the tested controller.** E.3's `createRealtimeWebRtcClient` originally delivered *normalized* events via a construction-time `deps.onEvent` — burying the parse→normalize wiring inside the manual-smoke shell. E.4b exposes a **settable raw `onServerEvent`** (the DC `onmessage`) + `sendClientEvent` (DC `send(JSON.stringify)`) and does parse→normalize→sink **in the controller** (unit-tested), removing `deps.onEvent`. Keep the exempt shell as thin as possible — push every decision into a tested unit.
2. **Interim realtime audio output (invariant #3).** The remote voice arrives via `pc.ontrack` (a `MediaStream`); the client singleton attaches it to a detached `<audio>` (`srcObject` + `play()`) so the translated voice is audible — **manual-smoke** (browser playback). The track is played LIVE, never captured/stored (invariant #3, consistent with E.4a's audioDelta-timing-only). `playback.started` is stamped **once** on the `<audio>` `playing` event (browser clock; `playing` re-fires → a once-guard, §12) — the realtime first-audio timing, **load-bearing** given the smoke-uncertainty that WebRTC may emit no `output_audio.delta` on the DC.
3. **RecordingControls dispatches by mode.** Start/Stop routes to `realtimeTurnController` when `currentMode==='realtime'` and the cascade `recordingController` when `'cascade'` — the realtime path's **real entry point** (`App.tsx` → RecordingControls → controller), closing the E.3/E.4a consumer-pending chain. Tested vs the real store + mocked controllers. The TranscriptPanel "source unavailable" (PRD must-have 6) already shipped at D.6/D.7 — E.4b adds characterization tests only (green-on-arrival, honest — no fail-first on pre-existing behavior).

**Rule:** Realtime turn control = a DI'd controller (`{ store, client, api, clock }`, the §11 analogue): createTurn→beginTurn→lazy-connect→per-turn sink wired to the client's raw `onServerEvent`→**buffer-delimited** manual turns (`session.update turn_detection:null`+`input_audio_buffer.clear` on Start; `commit`+`response.create` on Stop — the mic streams continuously, no per-turn toggle)→browser-clock `recording.started/stopped`→report on `responseDone`; dispatched from RecordingControls by `currentMode`; normalize lives in the tested controller (not the shell); interim audio = a detached `<audio>` (live track, never stored — #3) with a once-stamped `playback.started`.

---

## <a id="18"></a>18. Realtime connection lifecycle — a DI'd manager: one persistent idempotent pc, connecting@initiation, disconnect-surfaced, teardown-on-End

**Date:** 2026-05-30.
**Source slice:** E.5a (realtime connection lifecycle + disconnect + teardown). Discharges 4 carry-forwards (failed-turn errors, double-connect guard, the `connecting` stamp, `<audio>` lifecycle).

E.5a hoists the WebRTC connection from E.4b's per-turn lazy-connect to a **persistent pc**, via a DI'd `realtimeConnectionManager` (isolating the lifecycle + its tests; the turn controller delegates connect to `manager.ensureConnected()`, and E.5b's mode-switch/recovery extend the manager).

- **One pc, idempotent connect.** `ensureConnected()` connects once and holds the pc across turns (ARCH-010: `realtime_connect_ms` is a one-time cost); a 2nd call is a no-op (discharges the double-`connect()` orphan-leak guard). **⭐ The connect latch MUST reset on a FAILED connect** (and the failure rethrow) — else a transient connect failure latches "started" forever and permanently **bricks** realtime mode (a HIGH caught in review). On failure the controller catches → `failTurn` + aborts that turn; a later turn retries.
- **Connection timing.** `realtime.session.connecting` is stamped at connect **INITIATION** (browser clock — so `realtime_connect_ms = connected − connecting` measures the handshake; the B.3-origin marker); `realtime.session.connected`/`.disconnected` come from the pc `connectionstate` (a settable raw `onConnectionState` shell delegate → the tested mapper — same shell-vs-tested split as §17).
- **Disconnect surfaced, never swallowed (ARCH-018).** `connectionstate` `failed`/`disconnected` → stamp `realtime.session.disconnected` + a **frontend-synthesized** sanitized `UiError` (`{ code:'realtime.session.disconnected', stage:'realtime' }`, fixed-generic safeMessage — the raw connectionstate name is never interpolated) → `failTurn` (active turn — populating its `errors`, the B.9c-ii discharge) / `addError` (between turns); `errorCopy('realtime.session.disconnected')` renders the actionable **switch-to-Cascade** copy (§14).
- **Teardown on End + mode-switch-away (E.5b).** `manager.teardown()` (wired into SessionSetup's End onClick — idempotent, no-op for cascade) → `client.teardown()`: close DC/pc, stop tracks, release the stream, **detach + null the `<audio>` srcObject** (fresh element + fresh `playback.started` once-guard on the next attach), reset the connect latch. The actual pc/track ops are manual-smoke; the `manager.teardown`→`client.teardown` orchestration is unit-tested. **Flow-G mode-switch (E.5b):** `manager.onModeSwitch(from, to)` tears down **iff `from==='realtime' && to!=='realtime'`** (a switch AWAY from realtime) — so the realtime pc/mic/`<audio>` is released and doesn't linger while cascade captures its own mic (the **double-mic** bug); cascade→realtime reconnects lazily on the next turn (`ensureConnected`). The switch-away rule lives on the manager (the store reducer stays PURE — no transport side-effect in `updateSessionConfig`, ARCH-007); `ModeToggle`'s onClick calls it with `(state.mode, value)` + skips a same-mode no-op. _(The `ModeTransitionEvent` emit+persist — Flow-G timeline — is re-sequenced: backend `RecordModeTransition` is unwired + frontend-only team; needs a backend `POST …/mode-transition` slice. Not deliverable-blocking — the comparison uses each turn's `mode` field.)_

**Rule:** Realtime connection lifecycle = a DI'd `realtimeConnectionManager` owning ONE pc held across turns (idempotent `ensureConnected`; **the latch resets on a failed connect** so a later turn retries + the controller fails/aborts that turn); `realtime.session.connecting` stamped at connect-initiation, `connected`/`disconnected` from the pc connectionstate (a settable `onConnectionState` shell delegate → tested mapper); a disconnect is surfaced (sanitized `realtime.session.disconnected` UiError → failTurn/addError + errorCopy advise-switch, never swallowed); `teardown()` (close/stop/release/detach-`<audio>`/reset-latches) on End (SessionSetup) **and on `onModeSwitch` away-from-realtime** (no double-mic; the rule lives on the manager, the reducer stays pure).

---

## <a id="19"></a>19. Standalone EvaluationPanel — persist WER via a DEDICATED eval turn (createTurn → POST /wer w/ turnId); the eval turn is a backend-only artifact

**Date:** 2026-05-30.
**Source slice:** F.2 (`evaluation_panel` — the WER Evaluation panel, Flow D).

WER is persisted by the backend **only when `POST /api/evaluation/wer` carries a `turnId`** (it attaches the `WerResult` to that turn via `SessionStore.UpdateTurn` + a best-effort write — F.1a); the `transcribe` endpoint is stateless (no turn). So a **standalone** WER panel that must persist (for F.3's `WerSummary`) has to attach to a turn.

- **⭐ Create a DEDICATED evaluation turn per measurement — the flow ordering matters.** `record → transcribe → createTurn → computeWer({sessionId, turnId, phraseId, hypothesis})`. `createTurn` lands **AFTER** a successful `transcribe` (a failed transcribe must not orphan a turn); a `createTurn` failure → `addError` + abort (don't score against a turn that never got created — its own error arm, distinct from the transcribe arm). The eval turn is a **backend-only artifact** — its sole purpose is to be the `WerResult` attach point; the **frontend store's interpretation-turn machine (`currentTurn`/`turns[]`) is UNTOUCHED** (no `beginTurn`/`completeTurn`). The eval turn inherits the session's current mode, so it counts toward that mode's `ModeSummary.TurnCount` — **a documented limitation (the quality averages are null-skipping + unaffected; `WerSummary` is exact). A backend eval-turn marker (so the summary excludes them) is the clean fix, deferred.**
- **A `computeWer` failure AFTER `createTurn` leaves an orphaned eval turn** (a valid turn, no `WerResult`). Bounded for the single-trusted-user MVP; a backend turn-cancel/cleanup endpoint would close it. Documented in-code + carried forward.

**Rule:** To persist a result that the backend attaches to a turn from a **standalone** surface, create a dedicated turn as a **backend-only attach point** (after the producing call succeeds, with its own failure arm) and keep the frontend's domain-turn machine untouched; accept + document any aggregate-count skew the synthetic turn introduces (prefer a backend exclusion-marker as the clean fix); a producing-step failure after the turn exists leaves an orphan (bounded; track a cleanup endpoint).

---

## <a id="20"></a>20. One-shot eval capture — reuse `recordBlob()` (no 2nd MediaRecorder); a DI'd `evaluationActions` flow returns the transient result for local display, errors → store

**Date:** 2026-05-30.
**Source slice:** F.2 (`evaluation_panel`).

- **Reuse `audioCaptureController.recordBlob(durationMs)` for the one-shot eval recording** — it already exists (`Promise<BlobCapture {blob, mimeType} | null>`, probes the MediaRecorder mime per ARCH-030); don't add a second MediaRecorder path. A fixed duration (a named `EVAL_RECORD_DURATION_MS`) suits a scripted phrase. **`recordBlob` returns `null` SILENTLY on mic-denied/capture-fail** (unlike `startStreaming`, which has an `onError`) — so the **flow** must surface its own `capture.failed` `UiError` on the null (don't assume an upstream surfaced it). POST the blob multipart like `cascadeApi` (no content-type header — `fetch` sets the boundary; the backend strips MIME params + validates).
- **The DI'd `evaluationActions` flow RETURNS the transient `{hypothesis, werResult}` for LOCAL panel display** (not a store write) — the WER result is transient display state, not session state, so it doesn't belong in `UiSessionState` (diverges from `sessionActions`, which writes lifecycle to the store). **Errors still route to the store** (the single error sink, §2/§7); a `WerResponse.persistenceWarning` is surfaced via `addError` (degrade-don't-crash, mirrors `endSession`). The panel stays a thin render+dispatch (ARCH-007); a `useRef` **inFlight guard** on the handler covers the async double-click window the disabled button can't (§11). Gate the panel on an active session (reuse the existing `canStartRecording` selector — import, don't re-author) + fetch phrases on mount (sessionless `GET /phrases`).

**Rule:** Reuse `recordBlob()` for one-shot blob capture (it returns `null` silently → the flow owns the `capture.failed` surface); run the multi-step flow in a DI'd `*Actions` module that **returns** the transient result for local display while routing errors (+ a `persistenceWarning`) to the store; keep the component a thin render+dispatch with a `useRef` inFlight guard + an existing-selector gate.

---

## <a id="21"></a>21. Cost-by-model-variant comparison — a pure frontend aggregation over each wire turn's `costEstimate.model`; two sources, no double-truth; the `costEstimate`-not-`cost` gotcha

**Date:** 2026-05-31.
**Source slice:** F.3 (`comparison_summary` — the Realtime-vs-Cascade ComparisonSummary; closes Phase F).

The comparison needs **cost/min by mode AND by model variant**, but `ModeSummary` (`GET /summary`) is **per-mode only** (no model breakdown — `SessionSummaryService` doesn't group by model). The per-variant split is a **pure frontend aggregation over per-turn cost** (the §13 precedent — the backend prices each turn but doesn't pre-aggregate by model).

- **Two sources, each authoritative for its slice — NO double-truth.** Per-mode aggregates (avg latency, cost/min, errors, turn counts) + the session `WerSummary` come from `GET /summary` (canonical backend aggregate); the **per-variant cost split** is derived from the persisted turns of `GET /session`. Don't recompute the per-mode aggregates from the turns (that's `/summary`'s job) and don't try to get per-variant from `/summary` (it isn't there). Each variant row = `(mode, costEstimate.model)` grouped, averaging `estimatedUsdPerMinute` (cascade `model` = the translation model; realtime `model` = the realtime model, set by E.2b's `EstimateRealtime`).
- **⭐ The wire turn's cost field is `costEstimate`, NOT the viewmodel's `cost`.** `GET /session` returns the **raw** wire `InterpretationTurn` (camelCase of the C# `InterpretationTurn.CostEstimate` → **`costEstimate`**); the frontend's `TurnViewModel` renamed it to `cost` (a projection). Reading the raw wire turn must use `turn.costEstimate` — reading `cost` silently yields `undefined` → every variant null → the breakdown reads as "no priced turns" instead of erroring. A **focused `ComparisonTurn = {mode, cost: CostEstimate|null}` projection** (read only what the aggregation needs) keeps the opaque wire turn opaque (no full graduation); pin the field mapping with a **direct projection test** (`{costEstimate}` → `cost`; a turn with only the viewmodel `cost` and no `costEstimate` → `null`), not just a side-effect of the flow test.
- **Use `GET /session` (canonical), not the live `UiSessionState.turns`, for the per-variant source** — the realtime per-turn cost is computed backend-side at `/complete` and may not be written back to the live frontend turn; the persisted session has it for BOTH modes (+ survives a fresh load).
- **Honest degradation, three-valued.** `byVariant: VariantCost[] | null` — **`null`** = the per-variant source (`GET /session`) failed (degrades **independently** of the per-mode summary — a session-fetch failure leaves the headline intact, only the split goes "unavailable"); **`[]`** = ran but no priced turns ("No priced turns yet."); render the two distinctly. Skip **null AND non-finite** cost (`typeof === 'number' && Number.isFinite` — a malformed payload's `NaN`/`Infinity` would otherwise poison the average to `$NaN/min`), never a synthetic 0 (§9/§13). A null `ModeSummary` field renders **"n/a"** (never 0); **WER is unbounded** (`avgWer × 100`, never clamped past 100% — ARCH-015 / server §10).

**Rule:** Derive a by-model-variant breakdown as a pure frontend aggregation over each persisted turn's wire `costEstimate.model` (the backend prices per turn, doesn't pre-aggregate by model); source per-turn data from `GET /session` (canonical, both modes) + per-mode aggregates from `GET /summary` (each authoritative for its slice — no double-truth); read the **wire `costEstimate`, not the viewmodel `cost`** (pin with a direct projection test) via a focused projection that keeps the opaque turn opaque; degrade three-valued (`null` source-failed / `[]` no-data / rows), skipping null+non-finite cost (never a synthetic 0), "n/a" for missing fields, WER unclamped.

---

## <a id="22"></a>22. Styling slice — CSS/markup-for-styling only over store-driven components; a delivered design KIT's mock store is a visual reference, map to the REAL state + never copy mock fields

**Date:** 2026-05-31.
**Source slice:** H.1a (`ui_baseline` — the Phase H design-system styling foundation; manual-smoke, NOT `/tdd`).

A styling slice applies a delivered design system's visuals onto the shipped components. The discipline isn't RED-first (visual styling is manual-smoke-exempt, root TDD posture / ARCH-020 tier) — it's **clean-separation + green suites + preserved test queries**.

- **CSS + `className` + markup-for-styling ONLY — no logic/store/contract change** (clean-separation, ARCH-007 holds). Vendor the design's tokens (`colors_and_type.css` → `web/src/styles/tokens.css`, incl. the Google-Fonts `@import`) + adapt its stylesheet (`workbench.css` → `web/src/styles/workbench.css`), import both in `main.tsx`, add classNames + minimal semantic wrappers. **Plain CSS — no CSS Modules, no Tailwind** (match the design's own class-based idiom; transfers cleanly to later polish).
- **⭐ A delivered design KIT's mock store is a VISUAL reference ONLY — map to the REAL `UiSessionState`/selectors, never copy the mock's field names, never import its interactions.** The kit's `s.providerHealth === 'ready'` / direct `t.latency.X` / `s.summary.variants[]` are the mock's shape; ours reads `providerHealth: ConfigResponse` via the `modeAvailability()`/`availableModels()` selectors, computes top-level latency via `deriveTurnMetrics()` over `latencyEvents[]` (§13), and derives the per-variant split via its own `loadComparison()` (§21). Copying a mock field silently empties the UI. Likewise the kit's behaviors (conditional show-one-button, click-to-swap direction) are **logic changes** — keep our always-rendered + disabled-gated controls + the `<select>`.
- **Tests query role / `aria-label` / text / `data-final` — NOT className — so classNames + wrappers are safe additions; preserve every queried hook.** Keep button accessible names EXACT (`aria-hidden` a decorative `.sub` tagline so the ModeToggle name stays `"Cascade"`/`"Realtime"`). If matching the design would move an `aria-label`-bearing element, **adjust a query — never an assertion**; and if even a query-adjust would change *what* is asserted (e.g. a faithful by-mode comparison `.cmp-table` flattening the labeled `"Speech→first audio: 900 ms"` / `"Estimated $0.50/min"` context into bare mono cells → ~6 assertion rewrites), DON'T — choose the styling that keeps the assertions (style our mode cards in the blue=Realtime / violet=Cascade identity + apply the table treatment only where the data is already tabular, e.g. cost-by-variant). H.1a touched 0 test queries.
- **The ONE deterministic bit still gets a real test.** A pure latency-vs-target helper (mode + ms → good/warn/over/na; ARCH-013 thresholds cascade <3s / realtime <1.5s) is a shared pure function with spec values, so unit-test it (the `errorCopy` precedent) + don't inline-duplicate it across panels. A presentational `StatusPill` (value → token + REC-pulse/spinner/eqbars indicator) is display-only (manual-smoke); map any states the kit lacks (`captured`/`ending`) to the nearest token with a code comment.
- **Mode identity is a load-bearing visual contract:** Realtime = blue (`--bl-blue`), Cascade = violet (`--bl-violet`) — carried through the toggle, status accents, and the comparison; never swapped.

**Rule:** A styling slice restyles store-driven components with CSS/className/markup-for-styling only (clean-separation + green suites the disciplines, manual-smoke-verified, not RED-first); vendor the design's tokens + stylesheet as plain CSS and add classNames; treat a delivered kit's mock store as a visual reference — map to the REAL state/selectors + keep our behaviors, never copy mock fields or import mock interactions; preserve every role/aria-label/text/data-final the tests query (adjust a query never an assertion — and not even a query if it would change *what* is asserted); unit-test the one deterministic display helper (latency-vs-target), manual-smoke the rest.

---

## <a id="23"></a>23. Realtime first-audio / playback latency stamps are PER-TURN in the sink — never a session-`<audio>` once-stamp (the persistent-pc leak)

**Date:** 2026-05-31.
**Source slice:** G.4 smoke bug-fix (brief 049, Fix A) — the real-key smoke showed `speech-end → playback = -2102 ms` (negative). Refines §16 / §17.

- **The bug:** realtime `playback.started` was stamped on the **session-persistent** `<audio>` element's `onplaying` once-latch (reset only on teardown — End/mode-switch). The pc is persistent (§18), so `ontrack`→`attachRemoteAudio` fires ~once per session → the stamp is **session-level**, landing on whatever turn is `currentTurn` when the element first plays. Async, it can land on a LATER turn whose `recording.stopped` is after the stamp → a **negative** `speechEndToPlaybackMs`.
- **Stamp LEAK vs cross-clock skew (don't conflate):** the −2102 is within ONE browser clock (both markers are browser-clock `Date.now`), so it's a stamp leak — **fix the stamp**. It is NOT the intentionally-unclamped −50ms server-vs-browser skew that §13 / `selectors.test.ts:214` pins as disclosed-NOT-clamped. A blanket negative-clamp in `deriveTurnMetrics` would be WRONG (it'd hide the real skew disclosure).
- **Fix:** stamp realtime `playback.started` **per-turn in the event sink** (`realtimeEventSink`), on the turn's first post-stop `audioDelta` (alongside `realtime.first_audio_delta`), and DROP the session-`<audio>` `onplaying` stamp (the `<audio>` still PLAYS — it just no longer STAMPS). A fresh per-turn sink can't carry a prior turn's stamp. (§16: `audioDelta` is timing-only — the correct first-audio signal. This refines §17's "once-stamped `playback.started` on the detached `<audio>`": the once-stamp must be per-TURN, not session-once.)
- **Compounding latent bug:** `deriveTurnMetrics` read ONLY `tts.first_audio` for `speechEndToFirstAudioMs`, so the realtime headline was a **permanent n/a** — ARCH-013 documents the selection `tts.first_audio ?? realtime.first_audio_delta ?? playback.started`; the realtime fallback was unimplemented (doc-vs-code drift). Implementing the documented chain (cascade stays `tts.first_audio`-first → the −50ms cross-clock test stays green) fixed the realtime headline AND surfaced real comparison numbers.

**Rule:** Per-turn latency stamps for a mode whose transport is **session-persistent** (the realtime pc / its `<audio>`, §18) MUST be scoped per-turn — stamp in the per-turn event sink (§16), implicitly reset by a fresh sink; a session-level once-latch leaks a prior turn's stamp onto a later turn → negative/garbage deltas. Distinguish a stamp LEAK (within one clock → fix the stamp) from cross-clock skew (disclosed, never clamped — §13). And implement a metric's FULL documented selection chain (ARCH-013), not just its first source, or a mode's headline is a silent permanent n/a.

---

## <a id="24"></a>24. A status-gated mutating action must guard ALL non-live statuses (a not-live DENYLIST, not an active-only allowlist), self-clear its error before retry, and normalize any failure to one actionable code

**Date:** 2026-05-31.
**Source slice:** 054 (`mode_switch_error_recovery` — a user-reported live Finding: switching mode after the session ENDED POSTed to a dead `/mode` endpoint → 404 → the UI wedged ("must refresh")).

`switchMode` POSTs `/api/sessions/{id}/mode` for a live session, store-only otherwise. Three coupled bugs wedged the UI on a failed switch: (1) it guarded only `sessionId === null` (pre-session), NOT `sessionStatus === 'ended'` (and `sessionEnded()` doesn't clear `sessionId`) → a post-end toggle POSTed to a dead session → 404; (2) the error was never cleared on retry → the banner lingered → "must refresh"; (3) the failure hit the generic `errorCopy` fallback.

Load-bearing specifics:
- **⭐ Gate as a not-live DENYLIST, never an active-only allowlist.** Store-only when `sessionId === null || sessionStatus === 'ended' || 'ending'`; POST otherwise. A literal `=== 'active'` allowlist would silently SKIP the POST for a live `readyForTurn` session (a real `SessionStatus`, latent today) → re-introduce the 2c mode-divergence the moment a slice sets it. The denylist POSTs for ALL live states (active + readyForTurn), store-only only for genuinely-not-live. **Failure-mode asymmetry:** a future not-live status that POSTs-and-404s self-recovers (via the self-clear below); an allowlist's miss is SILENT data corruption — strictly worse. (The compound `||` early-return also preserves the `sessionId`→string TS narrowing in the `try`.)
- **⭐ Self-clear the error at the start of a real attempt — INSIDE the action, not the caller.** `switchMode` calls `store.clearErrors()` when it actually attempts a switch (like `startSession`), so a failed switch's banner can't linger and the UI self-recovers without a refresh. Putting it in the ACTION (not the `ModeToggle` onClick) makes it robust to every callsite (§7). `canToggleMode` doesn't gate on `errors`, so without this the toggle stays enabled-but-wedged.
- **Normalize ANY failure to ONE sanitized frontend code → actionable `errorCopy`.** A bare `catch {}` routes a single `session.mode_switch_failed` `UiError` (keep the prior mode — no divergence), so no raw `http.404`/backend code reaches the banner (invariant #4 strengthened — the raw error is unreferenceable) and `errorCopy` maps ONE code to actionable copy, never the generic fallback. Scope it to the one action (`startSession`/`endSession` keep their own ApiError handling).

**Rule:** A status-gated mutating action must guard **all** non-live statuses as a **denylist** (`null`/`ended`/`ending` → store-only; POST only when live — `active`/`readyForTurn`), not an active-only allowlist (which silently skips a live non-active status → divergence; the denylist's worst case self-recovers). **Self-clear the prior error inside the action** at the start of a real attempt (not the caller — robust to all callsites), and **normalize any failure to one sanitized code** mapped to actionable `errorCopy`, never the generic fallback. RED-first: ended/ending store-only-no-POST, the live-`readyForTurn`-POSTs regression-guard, self-clear-on-failed-switch.

---

## <a id="25"></a>25. Per-stage metrics are DURATIONS derived by differencing absolute-timestamp stage markers (keyed to the panel) — not a passed-through `relativeMs` map; anchor a pre-VAD speech-end on `stt.final`; badge responsiveness, never total-turn

**Date:** 2026-05-31.
**Source slice:** 056-c1 (`cascade_metrics_correctness` — a user live-smoke found the per-turn cascade metrics visibly wrong on a **completed** turn: per-stage all `n/a`, a negative responsiveness headline, the `<target` badge on the wrong metric).

`deriveTurnMetrics` (web §13/§16) computes the per-turn metrics the panel renders. Three coupled ARCH-013 derivation bugs:

Load-bearing specifics:
- **⭐ Per-stage values are DURATIONS, derived by DIFFERENCING stage markers — keyed to match the panel.** STT/Translation/TTS show `stt.final`−`cascade.audio.received` / `translation.final`−`translation.started` / `tts.complete`−`tts.started` (absolute-timestamp `Between`, §13), keyed `stt`/`translation`/`tts`. The bug: it passed the **raw `{eventName: relativeMs}` map** straight through, whose keys (`stt.final`, …) never matched the panel's stage keys → a **permanent `n/a`** even on a turn whose events were ALL present. A per-stage display must derive durations + key them to the consumer, never forward the event-name map. **Omit an absent/negative duration** (honest `n/a`, never a fabricated `0`).
- **⭐ Pre-VAD, anchor a "speech-end" metric on the endpointing signal (`stt.final`), not the manual stop.** `speech_end_to_first_audio_ms` (responsiveness) anchored on the **manual** `turn.recording.stopped`, which pre-VAD is held seconds after speech → `first_audio − recording.stopped` went **negative** (a misleading headline). Deepgram endpointing (`stt.final`) ≈ true speech-end → cascade responsiveness anchors on `stt.final` (fall back to `recording.stopped` when absent; realtime unchanged). Keep the **literal-`recording.stopped`** metric (`speech_end_to_playback_ms`) too — it stays backend-consistent (the `MetricsAggregator`/`ModeSummary` average uses `recording.stopped`); the two anchors are deliberately distinct. (Fully resolved only by Phase-I server-VAD.)
- **⭐ Badge the metric the threshold actually targets.** The `<target` badge (`<3s`/`<1.5s`) belongs on the **responsiveness** headline (both modes), NEVER on **total-turn** — which includes talk + post-speech hold time → a red badge on a turn that was actually responsive. Total-turn becomes a secondary row, **no badge**. `latencyTier` returns `na` for a negative value (the value stays disclosed; only the badge mutes — don't clamp the value, §23).
- **Backend-not-frontend residuals (flagged at Step 9, → the cascade-cost-backend queue):** cost `n/a` on a successful cascade turn (the C.2 live `usage`-nesting) + the **coincident `tts.started`==`tts.first_audio`** stamp (backend emits both at one instant → `0 ms`). Neither is a `deriveTurnMetrics` bug — don't paper over a backend stamp gap in the frontend.

**Rule:** A per-stage metric display must show **durations derived by differencing absolute-timestamp stage markers**, keyed to the consumer panel — never forward the raw `{eventName: relativeMs}` map (keys won't match → silent `n/a`); omit absent/negative (honest `n/a`, never a fabricated 0). Pre-VAD, anchor a "speech-end" responsiveness metric on the **endpointing** signal (`stt.final`), not the manual `recording.stopped` (held past speech → negative); keep the literal-`recording.stopped` metric distinct + backend-consistent. **Badge the metric the threshold targets** (responsiveness), never total-turn (talk+hold inflates it); `na`-tier a negative value but keep it disclosed. Backend stamp/cost gaps are backend follow-ups, not frontend patches.

---

## <a id="26"></a>26. Realtime cost finalizes at `/complete` carrying the DC `response.done.usage` exact audio-token counts — extract in the pure normalizer, dual-read the usage nesting, distinguish a real cached=0 from absent, never a synthetic $0

**Date:** 2026-05-31.
**Source slice:** 053-C2b (`realtime_cost_frontend` — the frontend half of the realtime cost `n/a` fix; the backend exact-count contract landed at 053-C2a `2977f7f`).

The backend can price a realtime turn from its EXACT audio-token usage (053-C2a: `CompleteTurnRequest` gained `inputAudioTokens?`/`outputAudioTokens?`/`cachedAudioInputTokens?`, and `EstimateRealtime` prices from them) — but **the realtime frontend never POSTed `/complete`**. It only reported latency via `appendTurnEvents` (`/events`); the realtime turn was finalized *in the store* (`sink.completeTurn`) but never *on the backend*, so the backend turn stayed non-terminal with a null `CostEstimate` and realtime cost rendered `n/a`. The fix wires `/complete` on `response.done`.

Load-bearing specifics:
- **⭐ Extract the usage in the PURE normalizer, not the controller.** `normalizeRealtimeEvent`'s `responseDone` variant gains `usage: RealtimeUsageTokens | null`; a guarded `extractRealtimeUsage` reads the three audio-token paths, each field independently number-guarded. The single classify point owns the wire→semantic mapping (§15/§16); the controller stays thin — it reads `event.usage` and POSTs `/complete` as an **independent sibling** to the existing `/events` report (the two are independent backend writes — `UpdateTurn` + idempotent `FinalizeTurn` — order-free). The sink stays **store-only + unchanged** (invariant #3 intact — only integer token COUNTS are read, never audio bytes); the cost is backend-priced and rendered via `GET /session` (§21), never the store.
- **⭐ Dual-read the usage nesting (`e.response?.usage ?? e.usage`).** GA `response.done` nests usage under `response.usage`, but a hand-captured fixture/runbook may flatten it (the `response:` wrapper trimmed for readability). A single guessed path that's wrong ⇒ `usage` always null ⇒ realtime cost stays `n/a` — i.e. the slice "fixes" the bug while silently reproducing it under green tests. Read BOTH nestings; the worst case is an honest null degrade, never a wrong cost. **Pin BOTH shapes deterministically** (a nested-`response.usage` test AS the production-primary + a top-level-`usage` test). This is the §15/§18 / server-§24 "verify the live GA shape — top-level vs nested" class; a tolerant dual-read is *stronger* insurance than a one-time doc check.
- **⭐ Distinguish a real `cached=0` from absent — never fabricate a 0.** A present `cached_tokens: 0` is a real value (send `0`); an absent field is omitted (not `0`). Absent/malformed `usage` → `null`, and the controller STILL POSTs `/complete` (so the turn finalizes terminal) but **without** token fields → the backend degrades to its absent-tokens path (disclosed-unavailable), never a synthetic `$0.00` (the §23/server-§25 honest-degrade posture — a priced `$0` reads as "free"). Internal `RealtimeUsageTokens` fields are `?: number` (absent=omitted) while the wire-mirror `CompleteTurnRequest` is `?: number | null` (faithful); the controller assigns only `!== undefined`, so `null` never flows.
- **A `/complete` failure is surfaced, never swallowed** — a rejected `completeTurn` routes a sanitized `realtime.complete_failed` `UiError` to the store (parallels the existing `realtime.report_failed`; ARCH-018). Map it in `errorCopy` so the banner isn't generic (§14/§24).
- **Out of scope (Step-9 carry-forwards):** the cached-token PATH precision (`input_token_details.cached_tokens` [the BE-agreed path 053-C2a priced from — FE/BE must agree on ONE path or silently disagree] vs the audio-specific `cached_tokens_details.audio_tokens`; immaterial while cached=0); finalizing a FAILED realtime turn at `/complete` (the `error` path never reaches `responseDone`); the reconnect-ordering edges (a `response.done` before `beginTurn`, or a late prior-turn `onServerEvent` posting for the new turn's id) — all → E.5 realtime-reconnect-lifecycle.

**Rule:** Wire realtime cost finalization at `POST …/turns/{turnId}/complete` (the realtime finalize; cascade is WS-priced, never here), forwarding the exact DC `response.done.usage` audio-token counts. Extract usage in the **pure normalizer** (single classify point) and POST from the controller as an independent sibling to the `/events` report; leave the store sink untouched (invariant #3). **Dual-read the usage nesting** (`response.usage ?? top-level usage`) and pin BOTH shapes — a wrong single path silently reproduces the `n/a` bug under green tests. **Distinguish a real cached=0 from absent**, degrade absent/malformed usage to null (still POST to finalize → backend discloses-unavailable), and never emit a synthetic $0. Surface a `/complete` failure sanitized.

---

## <a id="27"></a>27. Auto-VAD realtime (Phase I) — send `turn_detection: server_vad` in `session.update`, make auto-Stop a no-op (server owns segmentation), bound slice-1 to single-utterance via a per-turn settled latch; verify the server-VAD event sequence against live

**Date:** 2026-05-31.
**Source slice:** 063 slice 1 (`realtime_server_vad` — Phase I realtime, the toggle + config half; REVISES ARCH-003's locked "turn-based click start/stop, no VAD tuning" decision, user-approved "Phase I up").

Phase I adds automatic voice-activity detection while KEEPING the manual flow. The realtime VAD mode is **frontend-controlled** — the controller's per-turn `session.update` sets `turn_detection` (the backend mint only defaults it; the FE's `session.update` is authoritative), so the FE half needs NO backend change. A session-level `turnControlMode: 'manual' | 'auto'` (default `'manual'`) drives the branch.

Load-bearing specifics:
- **⭐ Auto mode = `turn_detection: { type:'server_vad', threshold, prefix_padding_ms, silence_duration_ms }` in `session.update` (GA defaults 0.5/300/500 as named constants) — and MUST still re-assert `transcription` (the 053-B clobber fix; else the source transcript regresses).** The server then auto-detects speech start/end + auto-commits + auto-creates responses. Manual mode unchanged (`turn_detection: null` + commit/response.create on Stop).
- **⭐ Auto-Stop is a NO-OP — do NOT send `input_audio_buffer.commit`/`response.create`.** With server-VAD the server owns segmentation; a manual commit/response.create RACES the server's auto-commit → a **double response**. (A *guarded* early-end — commit only if no `committed` event seen — needs the buffer events → deferred to the lifecycle slice.) This is the "defer to the evidence" case: the race argument overrode the brief's early-end default vote.
- **⭐ Bound a config-only slice to single-utterance via a per-turn `settled` latch.** Slice 1 reuses the EXISTING `response.done` path (verified) to finalize one turn — but the server keeps emitting events if the user keeps talking. A per-turn `settled` latch goes idle after the first auto `response.done` so a 2nd server-VAD'd utterance can't re-finalize (re-POST the §26 `/complete` + `/events`) or garble turn 1 by appending its deltas; the latch **resets on the next `startTurn`**. A **DEV `console.warn`** flags the dropped post-settled utterance so a live smoke catches the boundary (interim observability while the lifecycle slice is gated on a live capture — manual-smoke-exempt, like the 053-B DC logger). The documented limitation: one turn per Start; continuous multi-utterance (a turn per server-detected segment) is the lifecycle slice.
- **⭐ Split a verification-dependent feature so the safe half ships first.** Slice 1 (config + toggle + skip-commit + settled latch) is **event-sequence-INDEPENDENT** — it relies only on the verified `response.done` path, NOT the unconfirmed server-VAD buffer events (`speech_started`/`speech_stopped`/`committed`). So it ships WITHOUT a live server-VAD capture (our fixtures are all manual-mode/`turn_detection:null`). The `server_vad` CONFIG shape is Context7-confirmed; the buffer-event SEQUENCE is pinned in the lifecycle slice against a live capture (the §15/§26 verify-the-live-GA-shape discipline). Quarantine the live-sequence dependency + the sink change into the second slice.

**Rule:** Auto-VAD realtime sends `turn_detection: server_vad` (named-constant GA defaults) in `session.update` (re-assert `transcription`, 053-B) and makes auto-Stop a **no-op** (the server owns segmentation; a manual commit races the auto-commit → double response). Drive it off a `turnControlMode` store field (manual default, unchanged). When a config-only slice reuses the existing terminal path, **bound it with a per-turn settled latch** (idle after the first auto-terminal, reset on next Start) + a DEV warn on the dropped overflow; defer the multi-segment lifecycle (+ its sink change + the live-sequence verification) to a second slice. **Split a verification-dependent feature so the event-sequence-INDEPENDENT half ships first** without the live capture.

## <a id="28"></a>28. A realtime auto-VAD turn must NOT gate its lifecycle on the UNCONFIRMED `speech_started` — begin on the 053-C-confirmed `committed`/`response.created` too (guarded → one begin)

**Date:** 2026-05-31.
**Source slice:** 070 / Phase-I Bug C (`realtime_auto_vad_wiring`). The live both-modes smoke found cascade auto-VAD worked but **realtime never auto-finalized** the turn on end-of-speech.

**Root cause (the §15/§26 verify-the-live-GA-shape class, applied to a LIFECYCLE GATE):** the I.2-s2 realtime lifecycle (`06a604a`) handled the server-VAD events but gated the WHOLE turn-begin on `input_audio_buffer.speech_started` — an event the 053-C capture (MANUAL mode, `turn_detection:null`) NEVER live-confirmed. When it doesn't fire as expected, `beginAutoSegment` never runs → `currentSegmentTurnId` stays null → `response.done`'s finalize is gated out → the turn hangs open (audio plays, turn never closes).

**Fix:** decouple the finalize from the unconfirmed event — ALSO begin the segment on the **053-C-CONFIRMED** `input_audio_buffer.committed` AND `response.created` (in addition to `speech_started`), all routed through the guarded `beginAutoSegment` (the `segmentStarting`/`currentSegmentTurnId` guard collapses the triggers to EXACTLY ONE begin). So the turn reliably exists by `response.done` → finalizes through the SAME idempotent seam, whether or not `speech_started` fires. Preserve the 3A speech-end anchor in the fallback path (capture `pendingRecordingStoppedTs` on `speech_stopped` whenever there's no current turn). Manual + the auto early-end Stop unchanged. Reviewer-deferred to the live-capture hardening: a stale `pendingRecordingStoppedTs` between-segments anchor leak (could inflate `speechEndToFirstAudioMs`) + the `responseCreated`-only fast-`response.done` race (rarest fallback could re-hang).

**Rule:** Never gate a realtime turn's LIFECYCLE on an UNCONFIRMED GA event-string — begin the segment on the live-confirmed events too (`committed`/`response.created`), collapsed to one begin by the idempotency guard, so the turn finalizes regardless of whether the unconfirmed VAD event fires. A transport-timing/event-sequence dependency is browser-exempt (ARCH-020) → the live smoke catches the shape, not units. Extends §15/§26 (verify-the-live-GA-shape) to a lifecycle gate.

## <a id="29"></a>29. Never send a realtime client event before the RTCDataChannel is `open` — queue-until-open + flush-on-`onopen` in the WebRTC client (safe-by-construction; a no-op DROPS the config)

**Date:** 2026-05-31.
**Source slice:** 072 / Phase-I P0 (`realtime_dc_open_gate`). A live smoke (post-070) showed realtime **100% DEAD** — a definitive browser `InvalidStateError: RTCDataChannel.readyState is not 'open'` at `sendClientEvent`.

**Root cause:** `sendClientEvent` was `dataChannel?.send(...)` — the `?.` guards null, NOT `readyState`. `connect()` resolves (SDP handshake) before the DC reaches `open`, so `startAutoSession`/`startManualTurn`'s `session.update` send raced ahead → threw → `startTurn`'s promise rejected → the turn never initialized (BOTH modes). The connect→send ordering is the I.2-s2 structure (latent, first exercised live in auto mode); **orthogonal to 070** (its begin-trigger decoupling is untouched).

**Fix — the QUEUE, not a no-op:** a **pure** `createClientEventQueue({isOpen, rawSend})` → `{send, flush, clear}`: `send` → `rawSend` if open else BUFFER; `flush` (on the DC `onopen`) → drain IN ORDER; `clear` (on teardown) → drop (no stale replay across a reconnect). Wired into `realtimeWebRtcClient.sendClientEvent`. **⭐ A no-op-when-not-open would be WRONG** — it'd silently DROP the `session.update` → server-VAD never configured → still broken; the queue sends it AFTER open. The pure queue is unit-tested (buffer / flush-in-order / clear / no-throw + the fake-DC `onopen→flush` wiring pin); the DC plumbing is manual-smoke (the lead's live re-test revives realtime). Reviewer-deferred (LOW): a `MAX_PENDING` cap, a flush-rawSend-throw guard, null the DC handlers in teardown.

**Rule:** Never send a realtime client event (`session.update`/`response.create`/…) before the RTCDataChannel is `open` — buffer-until-open + flush-IN-ORDER on `onopen` in the WebRTC client (safe-by-construction; clear on teardown). A no-op-when-not-open is a TRAP (it drops the config silently). A transport-timing race is browser-exempt (ARCH-020) → the live smoke catches it, not units (the queue LOGIC is pure + unit-tested).

## <a id="30"></a>30. The GA Realtime `session.update` requires `session.type:"realtime"` on the session object — omit it → OpenAI rejects the update → no config applies → silent "ready"-hang (no crash)

**Date:** 2026-05-31.
**Source slice:** 073 / Phase-I P0 (`realtime_session_type`). The ROOT CAUSE of the entire realtime saga (bug C → 070 → 072 → 073) — **LIVE-CONFIRMED** fixed (user console: `session.updated` ACCEPTED, `turn_detection:server_vad` applied, both modes transcribe + translate + audio).

**Root cause:** post-070/072 realtime was 100% dead — OpenAI rejected EVERY `session.update` with `{"code":"missing_required_parameter","param":"session.type"}`. The payload was missing the GA-required `session.type:"realtime"` (the session discriminator, a sibling of `audio` ON the `session` object). Because the update was rejected, NO config applied → `turn_detection` stayed null → no auto-VAD, no transcription → "stuck at ready, 0 transcripts." Explains the WHOLE saga: bug C (no auto-VAD), the post-070/072 silence, AND manual mode failing — **the session was NEVER configured.**

**Fix:** add `type:"realtime"` to the `session` object of EVERY `session.update` (one line in the single `sessionUpdateInput` seam → covers manual, auto, auto-stop). **Context7-confirmed:** placement ON the `session` object (the `session.created` echo shows the session IS `type:"realtime"`); required on every update; the update is an **incremental patch** preserving the minted config (the `transcription` re-assert still rides) — no masked 2nd required field. KEEP the 072 DC-gate (correct — `session.created` proves the channel opens) + 070's begin-decouple (orthogonal).

**⭐ Process meta-lesson (verify-the-real-shape, then act on the live evidence):** this exact `session.type` hedge was FLAGGED at 070's Step-2.5 + DEFERRED per §18/§20 ("add only if the live capture shows zero VAD events") — and that was BOTH right to defer (don't speculatively change a confirmed-working payload — could've regressed) AND right to add the moment the live error NAMED the missing field. The architectural framing (our backend is NOT in the realtime transcription path — browser↔OpenAI direct; the Stop/ProcessReceiveQueue NREs were §30-server-cascade red herrings) correctly scoped it FE/config, not backend.

**Rule:** The GA Realtime `session.update` requires `session.type:"realtime"` on the `session` object; omitting it → OpenAI rejects the WHOLE update (`missing_required_parameter`) → no config applies → a SILENT "ready"-hang (no crash, 0 transcripts) that masquerades as a backend/lifecycle bug. Add it to every `session.update` (it's an incremental patch preserving the minted config). Verify-the-real-shape: defer a speculative payload change, but act decisively on the live error once it names the field.

## <a id="31"></a>31. A fair cross-mode `$/min` needs the SAME denominator basis both sides — supply the realtime denominator from recording duration, omit-never-0

Realtime turns showed a total `estimatedUsd` but `estimatedUsdPerMinute` was `null` — blanking the cost axis of the mode-vs-mode comparison — for ONE reason: the FE `/complete` finalize never sent `audioDurationMs`, so the backend's existing `CostEstimator.Build` (`perMinute = audioDurationMs > 0 ? usd/(audioDurationMs/60000) : null`) had no denominator. Fix = `finalizeTurn` sends the turn's **recording (source-speech) duration** (`recording.stopped − recording.started`, from the finalized turn's markers via `turns.find(turnId)`) as `audioDurationMs`. This computes `estimatedUsdPerMinute` on the **SAME source-speech-minute basis as cascade** (cascade passes its input/recording duration to the same `Build`) → apples-to-apples. **The input+output-audio basis was REJECTED** — it'd make realtime's denominator ~2× cascade's → not directly comparable; a fair cross-mode `$/min` REQUIRES the same denominator basis on both sides. Send `audioDurationMs` only when both markers are present AND `Number.isFinite && > 0`, else **OMIT** (honest-degrade → backend keeps `perMinute` null + discloses-unavailable; never a synthesized 0/negative denominator, §25/§26). Disclose the `$/min` basis in the cost UI (CostPanel disclaimer + ComparisonSummary). No backend change (`Build` already divides); no wire-shape change (`audioDurationMs` already on `CompleteTurnRequest`). (076)

## <a id="32"></a>32. Bidirectional direction-attribution is ASYMMETRIC by mode — cascade rides a MEASURED backend signal, realtime is a best-effort client heuristic; additive flags omit-when-false so one-direction stays byte-identical

Phase J (bidirectional auto-detect, J.3). When a turn's source language is auto-detected, how the FE learns the per-turn `direction` differs by mode and that asymmetry is load-bearing — don't paper over it with a uniform path. **Cascade = a MEASURED signal:** the backend resolves the direction from Deepgram nova-3 `multi`'s per-utterance language detection and emits a new `{type:"direction", direction:{source,target}}` WS message; the FE just consumes it (`dispatchCascadeMessage` → `setTurnDirection`) — no client guessing. **Realtime = a best-effort client HEURISTIC:** the Realtime API exposes NO per-utterance language tag, so the FE runs a deterministic `detectLanguage(text) → 'en'|'es'|null` over the **authoritative `sourceTranscriptCompleted`** transcript (NOT partial deltas — wait for the finalized utterance) and stamps `{source: detected, target: other}`; on `null` (ambiguous) it **falls back to the configured source** (the fallback policy lives at the CALL SITE, so the pure fn stays honest by returning `null`, and the call-site fallback gets its OWN test). The heuristic is a DISPLAY BADGE, not a measured signal — words that are common in BOTH languages (`'no'`,`'si'`) MUST be excluded from the Spanish set (they false-positive `'es'` on "no problem"); use ¿/¡ + diacritics (strong) then ES/EN stopword-density (tie/none → null). **Additive flags omit-when-false:** the cascade start frame's `bidirectional` and the realtime mint's `bidirectional` are spread only when truthy (`...(p.bidirectional ? {bidirectional:true} : {})`, mirroring `autoVad`/`model`) → the one-direction frame/body stays **byte-identical** (existing exact-shape `toEqual` tests stay green; back-compat with the backend's `default false`). Enabling bidirectional may default a paired flag (turn-control → Auto-VAD for hands-free) but keep them INDEPENDENT + gate the side-effect so it can't fire mid-turn (`canToggleMode`). (080)

## <a id="33"></a>33. Cascade continuous-listening = auto-trigger manual-mode's fresh-WS-per-turn on the auto-VAD COMPLETED terminal (FE-only, no BE change); the store's terminal status is the re-armable-vs-failed seam; close the double-arm window synchronously

Phase J completion (J.5) — making cascade auto-VAD a hands-free back-and-forth loop (the smoke found it required a manual Start per utterance while realtime looped). **The key realization: don't build a new multi-turn backend.** Cascade's manual mode ALREADY opens a fresh WebSocket per turn (every Start→Stop = new WS → one turn → server closes), so continuous-listening is just **auto-triggering that same fresh-WS-per-turn on the auto-VAD `done`** instead of waiting for a manual Start click — **FE-only, zero backend change** (the WS stays one-turn-per-connection by design; we reconnect per turn, NOT multi-turn-on-one-connection). The inter-turn reconnect gap (WS upgrade + Deepgram connect) is **masked by TTS playback** — the next speaker is listening to the translation during re-arm, so they don't start until after; the gap's stray mic frames hit the *closing* socket and are silently dropped per the WS spec (`send()` on CLOSING/CLOSED doesn't throw). **⭐ The re-armable-vs-ended discriminator is already in the store:** `completeTurn`/`failTurn` set `turnStatus` BEFORE the terminal hook (`onCascadeTerminal`) fires, so `turnStatus === 'completed'` (re-arm) vs `'failed'` (end + surface error) needs NO new wire signal / `onTerminal` payload — it stays `() => void`. Re-arm only when **auto + session-live + `!userEnded` + a live `captureHandle`**; the existing **Stop** button becomes the auto-mode end-conversation control (sets a `userEnded` controller closure — like the realtime controller's `autoListening`, NOT a store bit). **Keep the mic stream ALIVE across turns** (`startStreaming` once, re-open only the WS — the singleton client re-targets the new socket) to avoid a per-turn permission re-prompt + device re-init + to shrink the gap. **Close the double-arm/orphan window** (a concurrent manual Start during the async re-arm would call `startStreaming` on the live mic → null → orphan it): set `turnStatus('recording')` SYNCHRONOUSLY on re-arm (disables Start via `canStartRecording`) + re-check `userEnded`/liveness AFTER the async `createTurn` + reuse the `inFlight` guard + the one-terminal-per-turn latch; a re-arm `createTurn` failure must unstick to a recoverable `'failed'` (not a hung fake-`'recording'`). Realtime's persistent server-VAD loop is the UX template but cascade **reconnects per turn**. Manual mode unchanged. (If a live smoke shows a demo-noticeable gap, the seamless fallback is a BE continuous-orchestrator — Option B — scoped then; ship FE-only first.) (082)

## <a id="34"></a>34. For a cross-mode display, source mode-specific identity from the authoritative session-config `providerProfile` per mode — NOT a single-mode per-turn field; relabel single-model (realtime) stage rows to one note CONSISTENTLY across every panel

Phase J smoke Finding 2 (J.7) — two history/comparison display bugs, both about cross-mode display assumptions. **(2a Model) The history drill-in (`SessionDetail`) read each turn's model from `translationModelUsed` — a CASCADE-ONLY per-turn field that is NULL on realtime turns** → realtime Model rendered n/a even though the model is known. Fix: source the model per mode from the authoritative session config — `mode==='realtime' ? config.providerProfile.realtimeModel : (translationModelUsed ?? config.providerProfile.translationModel)` — the same source `ComparisonSummary` already uses (`comparisonActions` reads `providerProfile.{translationModel,realtimeModel}`). The session-config `providerProfile` (optional-chained — the wire always has `config`) is more robust than the per-turn `costEstimate.model` (which is null on a costless/failed turn). **(2b) Relabel single-model (realtime) stage rows to ONE "Single model — no discrete stages" note, CONSISTENTLY across every panel** — 074 did it for `MetricsPanel`, 085 for `ComparisonSummary`; three bare `STT/Translation/TTS-final: n/a` rows misread as "broken" when realtime genuinely has no discrete stages. **⭐ Verify an apparent display bug against the REAL persisted shape before "fixing" it** — the paired cascade Cost=n/a was a **NON-BUG**: completed cascade turns DO carry a per-turn `estimatedUsdPerMinute` (the existing `formatCostPerMinute(v.cost)` already shows it); the n/a the smoke showed was a genuinely-FAILED turn (`costEstimate:null` = honest n/a). A code-only diagnosis (the session JSON is runtime/gitignored) nearly shipped a session-average fallback that would have MASKED the true per-turn cost with the session rate — the real persisted JSON (via the lead) corrected the premise → dropped the dead fallback, pinned the already-correct behavior with characterization guards (like 084). No contract change. (085)
