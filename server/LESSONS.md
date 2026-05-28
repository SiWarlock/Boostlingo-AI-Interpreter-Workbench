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
