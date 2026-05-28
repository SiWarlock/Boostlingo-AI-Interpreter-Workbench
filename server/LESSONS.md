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
