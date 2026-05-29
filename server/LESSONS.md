# LESSONS.md — AI Interpreter Workbench (the .NET backend)

> Full prose for every lesson logged during work in `server/`. The compact index lives in `server/CLAUDE.md` "Lessons logged" table.
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

## <a id="1"></a>1. IOptions config-binding pattern — bindable types, inline defaults as source of truth

**Date:** 2026-05-28.
**Source slice:** A.2 (config/secrets/Options).

The provider `Options` classes (`DeepgramOptions`, `OpenAiTranslationOptions`, `OpenAiTtsOptions`, `RealtimeOptions`, `PricingOptions`) are **configuration-binding types**, not domain models — so they deliberately break from the ARCH-005 immutable-positional-record convention. `IOptions`/`ConfigurationBinder` binding needs **settable properties + a parameterless constructor**: use a `class` (or `record`) with `{ get; set; }` (or `{ get; init; }`) and inline default values; positional records do not bind. Treating these as immutable domain records would silently fail to bind.

**Inline defaults are the single source of truth.** Defaults live on the Options properties (`Model = "nova-3"`, `Encoding = "linear16"`, `ExpirySeconds = 600`, …); `appsettings*.json` does NOT duplicate them. Duplicating defaults across code + config is a drift trap — a *defaults-when-absent* test asserts the inline value while production reads the appsettings value, so a divergence passes tests but breaks production. With inline-only, "production with no env" equals exactly what the tests assert. The env vars (ARCH-028, documented in `.env.example`) are the operator config surface; the flat-env→section bridge + `services.Configure<T>(GetSection(SectionName))` wiring lands in A.5.

**Bind via `GetSection(s).Bind(new T())`, not `GetSection(s).Get<T>()`.** `Get<T>()` returns `null` for an absent/empty section, so it cannot prove inline defaults survive; `Bind` onto a fresh instance preserves the type defaults for absent keys and mirrors production `Configure<T>` semantics exactly — so the binding tests test what production does. Each Options class exposes a `const string SectionName` so the test, the A.5 DI registration, and the section name never drift apart.

**Test-project gotcha:** the `Microsoft.Extensions.Configuration.*` framework references flow in via `Microsoft.NET.Sdk.Web` on the API project but do **not** reach a non-web test project (`Microsoft.NET.Sdk`). The test project must reference `Microsoft.Extensions.Configuration`, `.Binder`, and `.Json` explicitly to bind config in tests.

**Rule:** Config Options are bindable types (settable props + parameterless ctor) with inline defaults as the single source of truth (appsettings does not duplicate them); bind via `Bind(new T())`, expose a `const SectionName`, and reference `Microsoft.Extensions.Configuration.*` explicitly in non-web test projects.

---

## <a id="2"></a>2. One shared JsonSerializerOptions for API + persisted JSON; round-trip tests use JSON-string equality

**Date:** 2026-05-28.
**Source slice:** A.3 (domain models).

The domain records (ARCH-005) serialize to both API responses (ARCH-009) and persisted session JSON (ARCH-016) — and those two surfaces **must not diverge**. So there is exactly **one** `JsonSerializerOptions` source: `Common/JsonDefaults` exposes `Options` (a pre-built instance for direct serialize/deserialize in persistence + tests) and `Apply(JsonSerializerOptions)` (called by the A.5 HTTP pipeline via `ConfigureHttpJsonOptions`). Both paths get camelCase property naming + `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` + explicit-null writing (`"summary": null` per ARCH-016, NOT `WhenWritingNull`). `Apply` is idempotent (guards against double-registering the enum converter when the framework hands it pre-seeded options). Never hand-roll a second options object for a new serialization site — reuse `JsonDefaults`.

**Record `==` is a trap for round-trip tests.** C# positional records get compiler-generated value equality, but for reference-type members (`List<T>`, `Dictionary<K,V>`) that equality is **reference-based** — so `original == Deserialize(Serialize(original))` is `false` even when every value matches. Round-trip fidelity tests therefore assert **JSON-string equality** (serialize → deserialize → re-serialize, compare the two JSON strings) plus targeted nested-field spot-checks, never record `==`. Keep the records exactly as ARCH-005 (mutable `List<T>` members) — don't switch to value-equal collections just to satisfy a test.

**Rule:** One shared `JsonSerializerOptions` (`Common/JsonDefaults`: camelCase + enum-as-camelCase-string + explicit-null) is the single source for API + persisted JSON; round-trip tests assert JSON-string equality, not record `==` (reference-based over collection members).

---

## <a id="3"></a>3. Degrade, don't crash, on optional external config (ARCH-018)

**Date:** 2026-05-28.
**Source slice:** A.4 (pricing config + loader).

Optional external config (here `pricing.json` via `PRICING_CONFIG_PATH`) must **never** take the app down when it is missing, unreadable, or malformed — it degrades to a documented "unavailable" state (ARCH-018) and the caller renders that gracefully ("estimate unavailable"). The resilient load order: (1) a `File.Exists` guard for the missing case; (2) a **size guard** (1 MB here) *before* `File.ReadAllText`, so a misconfigured huge-file path can't trigger an `OutOfMemoryException` — which must NOT be swallowed; (3) a **filtered** catch (`IOException`, `JsonException`, `UnauthorizedAccessException`, `SecurityException`, `ArgumentException`, `NotSupportedException`) — never a bare `catch (Exception)`, so fatal/programmer errors (OOM, NRE) still surface; (4) treat a null/empty deserialization result as a failure too.

The loader returns `Result<PricingOptions>` (the `Result` type from A.3); `Failure` carries a reason, but `Result.Error` is `[JsonIgnore]` (A.3) so the path or exception text can't leak to a client. Deserialize the file with the **shared `Common/JsonDefaults.Options`** ([§2](#2)) — the same camelCase/enum/explicit-null contract used for API + persistence — so a standalone config file binds identically. Don't hand-roll a second options object for a new load site.

**Rule:** Load optional external config with a missing-file guard + a size guard (before read) + a *filtered* catch (never bare; never swallow OOM/`SecurityException`) + null-result→`Failure`, returning `Result<T>` so the caller degrades to "unavailable"; deserialize via the shared `JsonDefaults`.

---

## <a id="4"></a>4. Wire host config in one place: flat-env→section bridge + single HTTP-JSON point

**Date:** 2026-05-28.
**Source slice:** A.5 (host wiring).

The ARCH-028 operator env vars are flat screaming-snake (`DEEPGRAM_API_KEY`); the A.2 Options bind from PascalCase config sections (`Deepgram:ApiKey`). Bridge them in **one place** in `Program.cs`: a single map from each flat var to its `Section:Property`, applied via `AddInMemoryCollection(...)` *before* `Configure<T>(GetSection(SectionName))`. Two rules make it safe: (1) **set only keys that are present** — an absent/blank env var must not write an empty override, or it would clobber the inline Options default (lesson §1); use `IsNullOrWhiteSpace` to skip; (2) a single shared key can **fan out** to several sections (`OPENAI_API_KEY` → `OpenAiTranslation`/`OpenAiTts`/`Realtime` ApiKey). Keep the bridge inline (not a new file) so the whole host-config story reads in one scroll.

Wire the **HTTP JSON pipeline through the single `JsonDefaults.Apply`** (`ConfigureHttpJsonOptions`) — never a second hand-rolled `JsonSerializerOptions` for HTTP — so API responses carry the identical camelCase/enum/explicit-null contract as persistence (lesson §2). Test it cheaply by asserting the resolved `Microsoft.AspNetCore.Http.Json.JsonOptions` has the expected naming policy + `JsonStringEnumConverter`, rather than spinning up a domain endpoint.

Integration tests of the host that depend on a **process env var** (e.g. proving the bridge) must set the real env var *before* constructing the `WebApplicationFactory` (its `ConfigureAppConfiguration` layers too late for top-level `CreateBuilder`-time code) and unset it in a `finally`. Serialize such tests (own xUnit collection / disable parallelization) so they can't race other env-reading test classes (e.g. B.9 config-presence).

**Rule:** Map flat operator env vars → Options sections in one `Program.cs` bridge (set only present keys so inline defaults stand; a shared key may fan out); wire HTTP JSON through the single `JsonDefaults.Apply`; host tests touching process env vars set-before-factory + finally-unset + run serialized.

---

## <a id="5"></a>5. Error-mapper boundary pattern — one owner; SafeMessage never echoes the exception

**Date:** 2026-05-28.
**Source slice:** B.1 (provider interfaces).

The exception→`ProviderError` mapping table (ARCH-012) lives in exactly one place — `Providers/Abstractions/ProviderErrorMapper.cs` — so the boundary error semantics (code, retryability, HTTP status) are defined once and every real provider (C) + the cascade orchestrator (B.4) routes through it. Two safety properties make it a clean boundary (ARCH-018/019):

- **`SafeMessage` is a fixed generic string per code; the mapper NEVER reads `ex.Message` / `ex.StackTrace` / `ex.Data` / `ex.ToString()`.** It inspects only the exception *type* and the numeric `HttpRequestException.StatusCode`. So no provider secret or stack frame can leak through the mapped error regardless of what the SDK throws. (The *full* sanitizer + server-side log of the original is B.8's `ErrorSanitizer`; the mapper is the first safe-by-construction layer.)
- **Don't throw inside the error-mapper.** The `stage` token is a closed set (`stt`/`translation`/`tts`/`cascade`) supplied as a compile-time literal by callers — enforce it with a caller-contract comment, not a runtime guard. A guard that throws inside the very code that exists to turn failures into safe errors would defeat the purpose.

Non-exception outcomes the orchestrator raises directly get explicit factories (`EmptyTranscript` → `cascade.empty_transcript` with `Stage="cascade"`, the cascade short-circuit; `Timeout` → `<stage>.timeout`), rather than synthesising a fake exception to feed `Map`.

**Rule:** Keep the exception→`ProviderError` table in one mapper; `SafeMessage` is fixed-per-code and never echoes the exception text/stack; validate the closed `stage` set by caller-contract (no throw inside the mapper); raise non-exception outcomes via explicit factories.

---

## <a id="6"></a>6. Streaming-fake pattern — paced async iterator with one cancellation point

**Date:** 2026-05-28.
**Source slice:** B.2 (fake providers).

The fake providers (and any streaming double) follow one shape: `async IAsyncEnumerable<TEvent> X(..., [EnumeratorCancellation] CancellationToken ct)`, choosing a behaviour by a constructor `enum` (e.g. `FakeSttBehavior.SuccessWithPartials`), with optional scripted payloads + a `delayPerEvent` (default `TimeSpan.Zero`). The `[EnumeratorCancellation]` attribute is what lets `await foreach (… .WithCancellation(token))` flow the caller's token into the iterator.

Pacing + cancellation live in **one** helper called before each `yield`: `FakeStreaming.PaceAsync(delay, ct)` = `ct.ThrowIfCancellationRequested(); await Task.Delay(delay, ct);`. The explicit `ThrowIfCancellationRequested` guarantees a deterministic `OperationCanceledException` **even at `Zero` delay** (where `Task.Delay(0)` may complete before observing the token), and the single `await` keeps the iterator from tripping the `CS1998` "async method without await" warning (a build error under warnings-as-errors). Error variants `yield return` a `*Failed(ProviderError)` then `yield break` (short-circuit — no `Final`/`Complete` after); the `ProviderError` uses a **real** ARCH-012 code (default `<stage>.upstream_unavailable`, scriptable), never an invented one.

Don't unit-assert the *wall-clock* delay (flaky); assert the **event ordering** + that cancellation throws. The same async-iterator + single-cancellation-point shape carries over to the real C providers consuming vendor streams.

**Rule:** Streaming fakes/providers are `async IAsyncEnumerable<TEvent>` with `[EnumeratorCancellation]`; route every per-event delay through one `PaceAsync(delay, ct)` (`ThrowIfCancellationRequested` + `await Task.Delay`) for deterministic cancellation + no CS1998; error variants `yield` a real-code `*Failed` then `yield break`; test ordering + cancellation, not wall-clock timing.

---

## <a id="7"></a>7. Metrics aggregation — absolute-Timestamp math, relativeMs is display-only, n/a-not-error

**Date:** 2026-05-28.
**Source slice:** B.3 (latency model + MetricsAggregator).

Two distinct time values live on a `LatencyEvent`, and conflating them breaks cross-clock metrics. `relativeMs` is a **per-event display value** — milliseconds from that event's documented reference origin, stamped by `LatencyEventFactory` and clamped ≥ 0 (a single event measured against a *same-clock* origin is monotonic, so a negative is nonsense). The aggregator must NOT compute metrics by subtracting two `relativeMs` values: cascade server stages carry `clockSource: server` while `turn.recording.stopped`/`playback.started` carry `clockSource: browser`, and `relativeMs` values relative to different origins/clocks are not comparable. Instead `MetricsAggregator` subtracts the absolute `Timestamp` (`DateTimeOffset`) of the two endpoint events — wall-clock instants meaningful across machines modulo skew (ARCH-013: "the aggregator must handle cross-clock pairs explicitly; a small skew is acceptable and disclosed").

Crucially the **factory clamps** a single event's `relativeMs` to ≥ 0, but the **aggregator does NOT clamp** a cross-clock metric pair — a small negative from browser/server skew is real, disclosed in the write-up, and hiding it with a `Math.Max(0, …)` would be dishonest. Two different rules in two different places; the suite pins the fork with a factory clamp-to-zero test (event before origin → 0) and an aggregator no-clamp test (server-behind skew → negative preserved).

A missing endpoint event yields `n/a` (null), **never an error** — a `nice`-tier event absent, or a whole metric family absent (a cascade turn has no `realtime.*` events and vice versa), drives the metric to null by absence, no mode parameter needed. The aggregator is pure + stateless + total (never throws, even on an empty list).

Two test-harness pins make the design self-enforcing and worth reusing: (1) every constructed `LatencyEvent` carries a deliberately-wrong `RelativeMs` sentinel (`999999`), so any aggregator that reads `RelativeMs` instead of `Timestamp` computes garbage and fails — proving the absolute-`Timestamp` rule; (2) tests use the literal ARCH-013 wire-strings (`"stt.final"`, …) while the aggregator keys off `LatencyEventNames` constants — so a constant typo → wrong lookup → null → test fails, pinning the vocabulary. Where ARCH-013 under-specified the final/complete + realtime metric origins, B.3 pinned them by the consistent per-stage-origin rule (now documented in ARCH-013's "Metric origins" note) and introduced `realtime.session.connecting` as the honest, null-until-emitted origin for `realtime_connect_ms` (E.4 emits it; no synthesis).

**Rule:** Aggregate latency metrics from absolute `Timestamp` (cross-clock safe); `relativeMs` is a per-event display value stamped by the factory (clamped ≥ 0), never a cross-event math input; the aggregator does NOT clamp cross-clock pairs (skew disclosed, not hidden); a missing endpoint → `null` (n/a), never an error.

---

## <a id="8"></a>8. Cascade streaming orchestrator — per-segment nested loop, stamp-on-first-arrival, arm/disarm idle timeout

**Date:** 2026-05-28.
**Source slice:** B.4 (cascade streaming orchestrator).

The `CascadeStreamingOrchestrator` realizes the ARCH-011 pipeline as a **nested per-segment loop**: `await foreach` the STT event stream, and on each non-empty `SttFinal` run *that segment's* translation→TTS sub-pipeline to completion (each stage streamed live) before resuming STT consumption for the next segment. This streams every finalized segment in arrival order without buffering the whole utterance (the streaming-honesty rule — a "consume all STT, then translate" structure full-utterance-blocks any multi-segment turn). Concurrent sub-utterance interleaving (segment-2 STT overlapping segment-1 translation) stays deferred (ARCH-025). The output is a flat, **transport-agnostic** `IAsyncEnumerable<CascadeOutputEvent>` (`Transcript`/`Latency`/`Audio`/`Error`/`Done`) — the orchestrator knows nothing about WebSockets or JSON; C.4 adapts it to the wire.

Each `first_*` LatencyEvent (`stt.first_partial`/`translation.first_token`/`tts.first_audio`) is stamped on **real first arrival** via a one-shot boolean gate (`LatencyEventFactory.Stamp`), never relabeled from a completion (lesson §7; forbidden-pattern #3). C# forbids `yield` inside a `try`/`catch`, so each stage **manually enumerates** its provider stream: `try { hasNext = await e.MoveNextAsync(); } catch(...)` wraps **only** the `MoveNextAsync`, and the `yield return` happens outside the try.

Two cancellation gotchas the reviewers caught (both fixed in-slice with regression tests):

- **A `CancellationTokenSource` that has fired cannot be un-cancelled** — so a per-stage `CancelAfter(timeout)` set once at loop-top is wrong for the STT stage: the timer keeps running during the (potentially long) per-segment translation/TTS sub-pipeline, fires mid-downstream, and the *next* segment's `MoveNextAsync` sees an already-cancelled token → spurious `stt.timeout`. Fix: make the STT timeout a **per-event idle timeout** — arm `CancelAfter(timeout)` immediately before each `MoveNextAsync`, disarm with `CancelAfter(Timeout.InfiniteTimeSpan)` immediately after, so only inter-STT-event gaps count. Translation/TTS keep a single whole-stage `CancelAfter` (nothing long is interleaved inside them).
- **Caller cancellation vs stage timeout must not conflate.** A linked-CTS timeout and a caller-cancel (client disconnect) both surface as `OperationCanceledException`. Mapping both to a retryable `<stage>.timeout` is wrong — a disconnect isn't a timeout. Filter the catch with `when (!ct.IsCancellationRequested)`: the caller's token cancelled → rethrow/propagate the OCE; only the stage timer fired → map to `ProviderErrorMapper.Timeout`.

**Rule:** Cascade orchestrator = nested per-segment loop (each `SttFinal` → its own streamed translation→TTS, sequential; concurrent interleaving deferred); stamp each `first_*` once on real first arrival; manual `MoveNextAsync` enumeration with try/catch only around the move (yield outside); STT timeout is a per-event idle timer (arm/disarm `CancelAfter`, since a fired CTS can't un-cancel); filter OCE with `when (!ct.IsCancellationRequested)` to split caller-cancel from stage-timeout; emit a flat transport-agnostic `IAsyncEnumerable<CascadeOutputEvent>`.

---

## <a id="9"></a>9. Cost estimation — branch on pricing basis, degrade via Result, 0.0 ≠ absent

**Date:** 2026-05-28.
**Source slice:** B.5 (cost estimator).

`CostEstimator` branches on the configured **pricing basis** rather than the provider: audio-minute (Deepgram STT), tokens (OpenAI translation), audio-output-tokens or characters (OpenAI TTS), and realtime (audio-seconds converted to tokens before the per-million rate). A usage abstraction (`CostUsage`, nullable fields) lets one entry point serve every basis; the basis selects which fields are read. Per-stage methods (`EstimateStt`/`EstimateTranslation`/`EstimateTts`/`EstimateRealtime`) are individually useful (F.1 STT-only eval cost) and compose into `EstimateCascadeTurn`, which sums the three stages into **one composite `CostEstimate`** (`Provider="cascade"`, `Model=<translation model>`, `PricingBasis="composite"`, breakdown in `Units`) — because the turn model + WS `cost` message are singular, the estimator (not the transport) owns the aggregation.

Three honesty rules keep estimates defensible:

- **`0.0` configured rate ≠ absent config.** A model present in `pricing.json` with a `0.0` rate estimates to `0.0` (a real, provisional number — e.g. `gpt-5.4-mini` pending build-confirm); only a genuinely-missing model / basis / usage degrades to `Result<CostEstimate>.Failure` ("estimate unavailable"). The distinction is a `decimal?` null-check on the rate, not a `== 0` check. Degrade follows lesson §3 (load via `Result`, never crash); the composite degrades **wholesale** if any stage can't price (no partial-garbage total).
- **Estimate factors are labelled estimates.** The realtime audio-seconds→tokens factor (`RealtimeTokensPerAudioSecond`) has no authoritative source yet, so it's a named constant, commented as an estimate, **surfaced in every realtime estimate's `Assumptions`**, and added to the build-time-confirm list — never presented as billing-grade. Tests reference the constant (pinning the *formula*), not its literal value, so a confirmed re-tune doesn't break them.
- **`decimal`, unrounded.** All cost math is `decimal` and the estimator does not round (the UI formats for display) — so tests assert exact value-equality and no precision is lost mid-pipeline.

**Rule:** Cost estimation branches on `PricingBasis` (not provider); degrade missing config/usage via `Result<CostEstimate>` (lesson §3), but a `0.0` configured rate is present and estimates to `0.0` (distinguish by `decimal?` null, not `==0`); estimate-only factors are named constants surfaced in `Assumptions` + flagged for build-confirm; compute in `decimal` without rounding; a cascade turn aggregates to one composite estimate keyed by translation model.

---

## <a id="10"></a>10. WER — normalize, DP-backtrace S/I/D, unbounded; reference is a precondition

**Date:** 2026-05-28.
**Source slice:** B.6 (WER calculator + phrase store).

WER is an **STT-transcript** quality signal, not semantic translation quality — keep the scope narrow. Normalization is **invariant**-lowercase → strip `\p{P}` Unicode punctuation (which covers inverted `¿`/`¡`) → collapse whitespace, with **accents/`ñ` preserved** (ARCH-015's accent-strip opt-in is NOT taken — Spanish text stays intact). Punctuation is removed (replaced with `""`, not a space) so contractions collapse (`don't`→`dont`); since STT may or may not emit apostrophes, removing them makes ref/hyp agree — but author the scripted `evaluation-phrases.json` free of intra-word punctuation (hyphens, contractions) so word boundaries are unambiguous.

The DP edit-distance keeps a **backtrace** to attribute S/I/D individually (the `WerResult` stores each, not just the total distance); the tie-break precedence is **match > substitution > deletion > insertion**, documented so counts are reproducible. Where a test must assert exact S/I/D, construct it tie-break-invariant (equal-length ref/hyp ⇒ insertions==deletions; a known LCS fixes the only composition) rather than depending on the backtrace path. **WER is unbounded** — more insertions/substitutions than reference words gives `WER > 1.0`, which is valid and must **never be clamped** (downstream metrics depend on the true value). An **empty normalized reference (`N=0`) is a precondition violation → `ArgumentException`** (the reference is always a validated scripted phrase), never a divide-by-zero or a clamped 0.

The phrase store self-loads once on construction via the lesson-§3 degrade pattern (missing / oversized / malformed → empty store + `LoadError`, never a host crash), exposing a clean DI facade (`Phrases`/`IsLoaded`/`LoadError`/`GetById`/`GetByLanguage`) over an internal `Result`-returning loader. Two security follow-ups for the F.1 boundary: the `n×m` DP matrix needs a hypothesis-length cap once a request body feeds it (memory-DoS), and `LoadError` carries path/`ex.Message` fragments so a controller must never surface it raw (same family as a `Result.Error`).

**Rule:** WER normalization = invariant-lowercase → strip `\p{P}` → collapse whitespace, accents preserved, punctuation removed (not spaced); DP backtrace attributes S/I/D with a documented tie-break (match>sub>del>ins); WER is unbounded (never clamp `>1.0`); empty reference is a precondition (`ArgumentException`, never divide-by-zero); the phrase store degrades via lesson §3 behind a DI facade; cap hypothesis length + never surface `LoadError` at the F.1 boundary.

---

## <a id="11"></a>11. Persistence safety — sentinel-scan, two-layer path guard, degrade-don't-crash

**Date:** 2026-05-28.
**Source slice:** B.7a (session store + persistence writer + sentinel).

The persistence writer is the point where an `InterpretationSession` becomes a file, so it carries the three never-persist safety invariants (root `CLAUDE.md` Key safety rules #1 standard keys / #2 ephemeral `ek_` secret / #3 raw audio) and the path-traversal guard (#5). Three patterns make those defensible and drift-proof:

- **Sentinel-scan, not just structural.** The never-persist guarantee structurally holds because the session model has **no field** for a key, the ephemeral secret, or audio bytes (it references neither `TtsAudioChunk.Bytes` nor `CascadeOutputEvent.Audio.Bytes`). A purely structural assertion is too passive — it can't catch a *future* field that leaks one. So the sentinel test injects sentinel patterns (`sk-…`, `ek_…`) into adjacent runtime state and scans the **serialized JSON text** for `sk-`/`ek_`/`bytes`/`apikey`/`clientsecret`, asserting all absent **and** that legitimate content (a transcript word, `stt.final`) is present (so it can't trivially pass by serializing nothing). The active scan is the drift defense. Tighten audio assertions to **key-form** (`"audio":`) so they can't alias an unrelated value like the `audioMinutes` cost unit. The writer is **not** a runtime sanitization boundary — it serializes the normalized model verbatim; scrubbing free-text content (a secret mistakenly placed in a `Metadata` value) is B.8's job, and the upstream invariant is that secrets/audio never enter the model at all.
- **Two-layer path-traversal guard; the trailing separator is load-bearing.** Layer 1: reject any `sessionId` not matching `^[A-Za-z0-9_-]+$` (ASCII allowlist via `char.IsAsciiLetterOrDigit`, before touching the filesystem — rejects dots, slashes, backslashes, unicode, percent-encoding, empty, whitespace). Layer 2 (defense-in-depth): canonicalize the resolved path with `Path.GetFullPath` and assert it `StartsWith` the **separator-terminated** `SESSION_DATA_DIR` full path. The trailing separator matters: without it, `…/data/sessions-evil` passes a `…/data/sessions` prefix test; with it, only paths genuinely under the root directory pass.
- **Degrade-don't-crash (lesson §3 family).** Persistence IO failure (unwritable dir, IO error) returns `Result.Failure("persistence.failed: …")`, never an unhandled throw — a failed save must not crash a turn or the stream. Per-turn writes are best-effort; the write-on-end is the MUST that surfaces success/failure (Flow F). `Result.Error` embeds `ex.Message` (which can contain an absolute path), so it is `[JsonIgnore]` and downstream HTTP/WS callers (B.9/C.4) must never echo it into a response body — pairs with the B.8 sanitizer.

Filename realization: `session_<StartedAt UTC yyyyMMddTHHmmssZ>_<short-id>.json`, timestamp from the session's `StartedAt` (stamped once at create, not write-time) so the per-turn + on-end writes **overwrite one stable file per session** rather than spraying N files.

**Rule:** Persistence safety = an active sentinel-scan (inject `sk-`/`ek_` patterns, assert absent from serialized JSON + legitimate content present, audio asserted key-form) backing the structural no-secret/no-audio-field guarantee; a two-layer path guard (ASCII allowlist pre-FS + canonicalized **separator-terminated** `StartsWith` under root); IO degrades via `persistence.failed`/`Result`, never crashes a turn; the writer serializes the model verbatim (not a sanitization boundary — that's B.8); one stable file per session via a `StartedAt`-derived filename.

---

## <a id="12"></a>12. Summary aggregation — reuse the per-turn seam, propagate null-semantics up, test purity by source-integrity

**Date:** 2026-05-28.
**Source slice:** B.7b (SessionSummaryService).

`SessionSummaryService` aggregates per-turn data into a session `SessionSummary` by **reusing the B.3 `MetricsAggregator` per turn** — it never re-implements latency math. Composing the existing seam (rather than duplicating the absolute-`Timestamp` logic) keeps one source of truth: a fix in the aggregator flows into the summary for free, with no second copy to drift. The same applies to cost — turns already carry `CostEstimate.EstimatedUsdPerMinute`, so the summary averages those (no `CostEstimator` dependency).

The per-turn → per-mode aggregation **propagates the null-semantics up the tier**: a metric averages over **non-null** values only, and is `null` (n/a) when absent on every turn — never `0`, never a throw (carried up from `TurnMetrics`/lesson §7). A whole `ModeSummary` is `null` iff its mode has zero turns (an empty mode is absent, not zero-filled); cascade-stage averages are `null` for realtime turns. WER averaging stays **unbounded** — a `>1.0` sample is included as-is (lesson §10); pin it with a fixture whose mean differs under a clamp (`[0.5, 1.5] → 1.0`, not `0.75`) so the no-clamp rule is load-bearing in the assertion. Cost/min is the **average of the mode's non-null per-turn per-minute rates** (a labeled estimate-of-estimates), not a blended `sum/sum` — the alternative mixes models within a mode; the model-variant breakdown is a consumer (F.3) concern from the raw turn list, not the mode-level summary.

A **pure compute function must be tested for source-integrity, not just output-correctness.** A "does not mutate" test that only asserts the computed field is still null can pass **trivially** (the field was null at construction). Strengthen it to assert the inputs are unchanged (turn-list count preserved) **and** that the computation actually ran (a populated result) — otherwise the purity guarantee isn't really pinned.

**Rule:** Aggregation/summary services compose the existing per-element seam (reuse `MetricsAggregator` / read each turn's `CostEstimate`; never re-implement the math); propagate "average non-null, absent→null, never 0/throw" up the aggregation tier (a sub-summary is null iff its group is empty); keep WER unbounded in aggregates and pin no-clamp with a clamp-sensitive fixture; a pure-compute test asserts source-integrity (inputs unchanged + the computation ran), not just absence of the field write.

---

## <a id="13"></a>13. Error sanitization — safe-by-construction, log original server-side single-lined, project-don't-duplicate

**Date:** 2026-05-28.
**Source slice:** B.8 (ErrorSanitizer + UiError).

`ErrorSanitizer` is the single boundary that turns any internal error (`Exception`, `ProviderError`, failed `Result`) into a client-facing `UiError` (safety invariant #4 — no stack/secret/raw-payload reaches a client; ARCH-018/019). The robust design is **safe-by-construction, not scrub**: the safe message is ALWAYS a **fixed string per code** (`SafeMessageForCode` — a small map + a generic fallback), and the sanitizer **never interpolates** untrusted input (`ex.Message` / `Result.Error` / stack / provider payload) into the output. Active regex-scrubbing of the original is brittle and fails open; constructing the output purely from fixed strings fails closed. The test proves it by injecting a sentinel secret (`sk-…` / `ek_…` / a filesystem path) into the input and asserting it's absent from **every serialized field** of the `UiError` (serialize + scan the JSON, not just `SafeMessage`).

Three supporting rules:

- **Log the original server-side — single-lined.** The full original (message + stack / `Result.Error`) is logged via the injected `ILogger` at the sanitize point so diagnosis stays possible; the client gets only the safe `UiError`. **Single-line the logged string first** (`ReplaceLineEndings(" ")`) — a multi-line internal error (or attacker-influenced text) could otherwise forge fake log lines (log injection).
- **Project, don't duplicate.** An already-safe `ProviderError` (built by `ProviderErrorMapper`, lesson §5) is **projected** to `UiError` (carry code/safeMessage/stage/retryable; **drop** the `Provider` identity); the sanitizer owns only the generic `Exception` + `Result` boundary and does **not** re-implement the ARCH-012 HTTP-status table. One owner per table.
- **Status off the wire body.** The TS `UiError` (ARCH-007) has no status field — HTTP status is the response status line. The backend record carries a server-only `[JsonIgnore] int? HttpStatusCode` (null → 500 at the handler) so the body stays an exact TS mirror while the status is preserved for B.9's handler. Caller contract: the `code` on the `Result` path is a normalized literal, never request-derived (it becomes `UiError.Code`).

**Rule:** Error sanitization is safe-by-construction (fixed safe message per code; NEVER interpolate `ex.Message`/`Result.Error`/stack/payload into a client-facing error; prove via inject-secret-assert-absent across all serialized fields); log the original server-side only, single-lined to prevent log forgery; project an already-safe `ProviderError`→`UiError` (drop provider identity) and reuse `ProviderErrorMapper` rather than duplicating its table; preserve HTTP status via a server-only `[JsonIgnore]` field so the wire body stays an exact TS mirror.

---

## <a id="14"></a>14. Global exception handler — thin `IExceptionHandler`, `AddProblemDetails` gotcha, emit `UiError` not `ProblemDetails`

**Date:** 2026-05-28.
**Source slice:** B.9a (global exception handler).

The unhandled-exception path is the *other half* of safety invariant #4 (lesson §13 is the sanitizer; this is its HTTP-boundary application). A `GlobalExceptionHandler : IExceptionHandler` (the .NET 8 idiom — DI-injectable, unit-testable) catches anything that escapes an endpoint, routes it through the **one** `ErrorSanitizer` (never re-sanitizes / re-logs — the sanitizer already logged the original single-lined), and writes a safe `UiError` JSON body with status `UiError.HttpStatusCode ?? 500`. Without it, an unhandled exception falls through to a framework error page — a **stack trace in Development** — straight to the client.

Wiring + gotchas (all non-obvious, all worth banking):

- **`AddProblemDetails()` is required** for the parameterless `app.UseExceptionHandler()` to initialize — without it the host **throws at startup**. Our handler always writes a `UiError` and returns `true`, so the ProblemDetails fallback writer is never reached — but the registration is mandatory.
- **Side effect of `AddProblemDetails()`:** framework-level routing errors (**404 not-found / 405 method-not-allowed**) now emit `application/problem+json`, **not** `UiError`. Acceptable for unrouted paths the SPA never calls (a 404/405 is a bug or a probe, not a normal SPA path); add `UseStatusCodePages`→`UiError` only if a real consumer needs the uniform contract. No leak (ProblemDetails carries no stack/secret).
- **Emit `UiError`, never `ProblemDetails`** for the handled path (ARCH-009 — the frontend `ErrorBanner` consumes `UiError`). Serialize with the **explicit** `JsonDefaults.Options` (don't rely on ambient pipeline options inside the handler).
- **Place `app.UseExceptionHandler()` first** (right after `Build()`, before Swagger/WebSockets/CORS/endpoints) so it wraps the whole pipeline.
- **Test by unit-testing the handler class** (`DefaultHttpContext` + a `MemoryStream` body) — strong, no production test-route. A throwing-route integration test is fiddly in minimal-hosting (IStartupFilter ordering vs the endpoints); prove the end-to-end wire instead by **overriding a registered service with a throwing fake** (`ConfigureTestServices`) and hitting a real endpoint once one exists.

**Rule:** The global exception handler is a thin `IExceptionHandler` that routes unhandled exceptions through the one `ErrorSanitizer` → `UiError` (never `ProblemDetails`, never re-sanitize/re-log), placed first in the pipeline; `AddProblemDetails()` is mandatory for parameterless `UseExceptionHandler()` startup-init and as a side effect makes framework 404/405 emit ProblemDetails (accept for unrouted paths); unit-test via `DefaultHttpContext`, prove the end-to-end wire via a throwing-service override at a real endpoint (not an artificial route).

---

## <a id="15"></a>15. Controller conventions — thin controller + interface test seam, MVC JSON options, capability-from-key-presence

**Date:** 2026-05-28.
**Source slice:** B.9b (ConfigController + ConfigService).

The first MVC controller establishes the conventions every later controller (B.9c sessions, F.1 evaluation) inherits:

- **MVC JSON options are SEPARATE from the minimal-API ones.** A.5 set the minimal-API contract via `builder.Services.ConfigureHttpJsonOptions(...)`, but MVC controllers serialize through `Microsoft.AspNetCore.Mvc.JsonOptions` — a **different** options object. Carry the shared contract onto the MVC path explicitly: `builder.Services.AddControllers().AddJsonOptions(o => JsonDefaults.Apply(o.JsonSerializerOptions))`. Without it the controller path silently drops the enum-as-camelCase-string + explicit-null contract — invisible for string/bool DTOs (`ConfigResponse`) but wrong for enum-bearing DTOs (`InterpretationMode`/`TurnStatus`/`SessionStatus`). Minimal-API endpoints (health) keep the `ConfigureHttpJsonOptions` contract; both coexist.
- **Thin controller → service (ARCH-008); the controller's collaborator gets a thin interface as the test seam.** The codebase's services are otherwise concrete (no interfaces), but the controller→service edge is the one place an interface earns its keep: it's the idiomatic MVC testability boundary and the only clean way to substitute a *throwing* collaborator to prove the global exception handler catches on the real path (`ConfigureTestServices` swaps `IConfigService` for a throwing fake → real endpoint → sanitized `UiError`). Prefer the interface over un-sealing + a `virtual`-for-test method (a recognized smell). Model-binding errors yield 400 ProblemDetails, not an unhandled exception, so this substitutable seam is the *only* way to exercise `UseExceptionHandler`.
- **Capability/config endpoints report from key PRESENCE, never the value.** `configured = !string.IsNullOrWhiteSpace(ApiKey)` (invariant #1, ARCH-019); the response carries booleans + operator-config identifiers (model names, provider strings) but never a key value. Pin it with a **no-secret-echo sentinel scan**: set sentinel key values in config, assert the serialized body contains neither (and that legitimate content IS present, non-trivial). Defensive-copy any shared static catalogs into each response (`[.. Catalog]`) so a `string[]` surface can't alias a mutable static.

**Rule:** Controllers are thin and delegate to a service; the controller's collaborator gets a thin interface as the test seam (substitutable throwing fake proves `UseExceptionHandler`, cleaner than `virtual`-for-test); `AddControllers().AddJsonOptions(JsonDefaults.Apply)` carries the shared JSON contract onto the MVC path (separate from the minimal-API `ConfigureHttpJsonOptions` — load-bearing for enum DTOs); capability/config endpoints report from key presence (`!IsNullOrWhiteSpace`) never the value, pinned by a no-secret-echo sentinel scan, with static catalogs defensive-copied per response.

---

## <a id="16"></a>16. Controller request/response boundary — `Result`→DTO never-serialize, record-param validation gotcha, no path disclosure

**Date:** 2026-05-28.
**Source slice:** B.9c-i (session-lifecycle endpoints).

The controller↔HTTP boundary is where internal types become wire DTOs; three conventions keep it safe + correct:

- **`Result`/`Result<T>` → DTO at the boundary; NEVER serialize a `Result`.** Map success → `Ok(dto)` (or the success response shape) and failure → a sanitized `UiError` via `ErrorSanitizer.SanitizeResult`/`SanitizeResult<T>` (logs the original server-side, single-lined). An internal outcome wrapper that carries a `Result` (e.g. `EndSessionOutcome(session, Result<string> persist)`) stays **internal** — the controller projects it to a serializable DTO. For an **expected** condition (a not-found, not an exception or a `Result`), use `ErrorSanitizer.ForCode(code)` — it builds a `UiError` from the owned `SafeMessageForCode` map with **no Error log** (a 404 isn't an error to log), keeping the sanitizer the single owner of safe messages. A controller-returned 404 (routed path, missing resource) is a `UiError`; the framework 404/405 for *unrouted* paths stays ProblemDetails (lesson §14).
- **DataAnnotations on a record positional parameter target the PARAMETER, not the property.** `public sealed record CreateSessionRequest([Required, MaxLength(200)] string RealtimeModel, …)` — NOT `[property: Required, MaxLength(200)]`. MVC's validation metadata provider throws `InvalidOperationException` ("validation metadata must be associated with the constructor parameter") on the property-target form. Caught only by a full-suite run that actually boots MVC model binding.
- **Boundary input caps + no response path disclosure (ARCH-019).** Request DTO strings get `[MaxLength]`/`[Required]` caps (an unbounded model/label string is a DoS / unbounded-disk-write vector once it reaches the store + persistence — even in a no-auth workbench, boundary hygiene). A response that echoes a server path discloses it: return **filename-only** (`Path.GetFileName`), never the absolute path / data-dir / username (the user already knows `SESSION_DATA_DIR` from config).

**Rule:** Map `Result`/`Result<T>`→DTO at the controller boundary and never serialize a `Result` (success→`Ok(dto)`; failure→sanitized `UiError`; expected conditions→`ErrorSanitizer.ForCode`, no log; internal outcome wrappers stay internal); put DataAnnotations on record **parameters** not properties (MVC throws otherwise); cap request strings with `[MaxLength]`/`[Required]` (ARCH-019 boundary hygiene) and return filename-only paths (no absolute-path/data-dir disclosure).

---

## <a id="17"></a>17. Provider-boundary contract suite — interface-level + provider-agnostic, error-code preservation is the contract

**Date:** 2026-05-29.
**Source slice:** B.10 (provider boundary tests).

The ARCH-020 CRITICAL `ProviderBoundaryTests` is a **contract-conformance suite**, distinct from the lower layers it sits above: B.1 `ProviderErrorMappingTests` pins the exception→`ProviderError` mapper *truth table* in isolation; B.2 `FakeProvidersTests` pins each *fake's* per-variant behavior; B.4 pins the *orchestrator's use* of the boundary. The boundary suite pins the contract that **any** provider — fake now, real in C.5 — must honor, so it's written **at the interface level** (`ISttProvider x = new FakeSttProvider(...)`, not the concrete fake) and C.5 extends the same file by swapping in real providers + reusing the assertions/helpers (a local `Collect<T>` + request builders, co-located for that reuse).

The genuinely net-new assertion is **error-code preservation**: a `*Failed` event carries the **real ARCH-012 `ProviderError`** it was scripted with — `Code`, `Retryable`, `Stage`, **and `Provider`** survive the boundary unchanged (B.2 only checked the code is non-empty). Script it from an actual `ProviderErrorMapper.Map(...)` output across stages incl. a non-retryable case (403→`*.auth`, `Retryable=false`) so `false` is proven preserved, not just `true`. Don't re-derive B.1's truth table or re-run B.2's per-fake ordering verbatim; cancellation is B.2's (per-stage mid-stream + pre-cancelled) — the materially-different real-provider HTTP-stream cancellation belongs in C.5, not a near-verbatim re-run here.

Make the success contract **tolerant of conformant variants** so it survives real-provider reuse: assert "`FirstAudio` present; **if** chunks follow, it precedes them" — NOT a hard `FirstAudio < firstChunk` (a real TTS provider may emit `FirstAudio→Complete` with no chunks; the `CompleteOnly` fake variant proves that shape exists). A contract test that rejects a conformant shape is worse than no test.

**Rule:** A provider-boundary contract suite is written at the **interface level** (provider-agnostic) so the same assertions run against fakes (B) + real providers (C.5 extends the file); its net-new pin is **error-code preservation** (a `*Failed` carries the real `ProviderError` `Code`/`Retryable`/`Stage`/`Provider` unchanged — incl. a non-retryable case), NOT a re-test of the mapper truth table (B.1) / per-fake behavior (B.2) / cancellation (B.2); keep success assertions tolerant of conformant variants (no-chunk TTS success) so they survive real-provider reuse.
