---
name: boostlingo-workbench-design
description: Use this skill to generate well-branded interfaces and assets for the Boostlingo AI Interpreter Workbench (and adjacent Boostlingo surfaces), either for production or throwaway prototypes/mocks/etc. Contains essential design guidelines, colors, type, fonts, iconography, and a UI kit for the instrumented Realtime-vs-Cascade comparison dashboard.
user-invocable: true
---

# Boostlingo AI Interpreter Workbench — design skill

Read **`README.md`** first (product context, content fundamentals, visual foundations, iconography,
CAVEATS, and a full file index). Then explore the other files as needed:

- **`colors_and_type.css`** — import this for all design tokens (color, type, spacing, radius, shadow)
  + base semantic styles. Everything keys off these CSS custom properties.
- **`UX_SPEC.md`** — the authoritative layout & UX contract: the 11 component regions, the two state
  machines (`sessionStatus`, `turnStatus`), the selectors that gate the UI, the field-level data
  model, the dashboard layout, and the key flows. Follow this for anything Workbench-shaped.
- **`preview/`** — small specimen cards (colors, type, spacing, components) that show the system in use.
- **`ui_kits/workbench/`** — a high-fidelity, interactive recreation of the dashboard. Lift its
  components (`components.jsx`, `panels.jsx`) and styles (`workbench.css`) as a starting point.
- **`assets/`** — brand mark / lockup (currently **placeholders** — see README CAVEATS).

## How to use it
- **Visual artifacts** (slides, mocks, throwaway prototypes): copy the assets you need out into static
  HTML files for the user to view; reuse the tokens + UI-kit components.
- **Production code**: copy assets and internalize the rules here to design on-brand. Honor the
  constraint that the Workbench is **restyle + rearrange only** — keep the 11 component boundaries.

## Non-negotiables for this product
- It's an **instrument / evidence tool**, not a consumer app. Calm, data-forward, Linear/Vercel-grade
  restraint. The **measured numbers are the hero** — mono tabular numerals everywhere.
- **Realtime = blue, Cascade = violet.** Carry these two identities through every surface, especially
  the comparison grid. Never swap them.
- **Visualize state**: active-mode highlight, color-coded status pills + live indicators, clear
  enabled/disabled affordances, partial-vs-final transcript treatment, latency-vs-target coloring,
  always-qualified "Estimated $/min", and `n/a` (never a fake 0 or blank).
- Copy is **sentence case, impersonal, precise**. No emoji in-product. Icons are **Lucide**, 1.75 stroke.

If the user invokes this skill without other guidance, ask what they want to build, ask a few focused
questions, then act as an expert designer who outputs HTML artifacts **or** production code as needed.
