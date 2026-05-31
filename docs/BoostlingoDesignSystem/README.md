# Boostlingo AI Interpreter Workbench — Design System

A design system for the **Boostlingo AI Interpreter Workbench**: a single-screen, instrumented
"workbench" dashboard for running live speech-interpretation turns in two modes
(**Realtime** vs **Cascade**) and comparing them on **latency, cost, and quality (WER)**.

It is an **evidence / comparison tool**, not a consumer interpreter. The hero content is the
**measured numbers** — not the conversation. This system gives a design agent everything needed
to recreate, restyle, and extend that workbench (and adjacent Boostlingo surfaces) on-brand.

---

## About Boostlingo

[Boostlingo](https://boostlingo.com) is a language-services software & technology company based in
**Austin, Texas** ("Communicate Without Barriers"). It runs an all-in-one interpreting platform —
on-demand interpreting (OPI / VRI), an Interpretation Management System (IMS), multilingual events
(RSI, via VoiceBoxer), and **Boostlingo AI Pro** for AI captioning, transcription, and speech
translation. In **Aug 2025** it launched the beta of its **AI Interpreter** — real-time, context-aware
interpreting. The Workbench in this project is the internal evidence tool that sits behind that
AI Interpreter effort.

The company refreshed its **brand identity** alongside its AI products. The brand centers on a vivid
"Blue Ribbon" blue with a warm coral accent ("Trinidad"), a lowercase geometric wordmark, and a
circular "bold circle / ribbon" brandmark.

### Sources used to build this system
- **Product spec** — the detailed Workbench brief supplied with this project (component inventory,
  the two state machines, selectors, field-level data model, proposed dashboard layout, flows). This
  is the **source of truth** for layout & UX. See `UX_SPEC.md` (copied verbatim from the brief).
- **Aesthetic reference** — `uploads/Original Image 2048x1536.webp` (a "Digesto" dashboard mockup).
  **Used for visual aesthetic ONLY** — rounded cards, soft shadows, pastel category tints, dark "pill"
  stat chips, vivid-blue primary actions, near-black secondary buttons, friendly geometric type, big
  bold numerals, generous whitespace. **Not** used for layout or UX (those come from the spec).
- **Brand research** — boostlingo.com (homepage, solutions, LSP pages), LinkedIn, Slator, Brandfetch.
  Brand asset URLs captured below.

### Brand asset URLs (could not be downloaded here — cross-origin)
> These are on Boostlingo's CDN. I was unable to fetch binary assets into the project. The logos in
> `assets/` are **faithful HTML/CSS recreations / placeholders** — see CAVEATS. Please drop the real
> files in to replace them.
- Wordmark: `https://boostlingo.com/wp-content/uploads/2024/01/boostlingo-logo.svg`
- Logo mark (full color): `https://boostlingo.com/wp-content/uploads/2024/01/boostlingo-logo-mark-full-color-rgb-1.svg`
- Bold circle / blue-ribbon mark: `https://boostlingo.com/wp-content/uploads/2024/01/boostlingo-bold-circle-blue-ribbon-full-color-rgb.svg`
- Solution icons (gradient line): `.../2024/01/human.svg`, `.../intrepretation-management-icon.svg`,
  `.../ai_icon.svg`, `.../Translation-Solution-Icon_Gradient.svg`

---

## The product in one paragraph

One mode-agnostic UI. The operator picks **Realtime** (OpenAI Realtime over WebRTC) or **Cascade**
(STT → Translation → TTS). Language pair **EN ↔ ES**, both directions. A **turn** = Start → speak →
Stop → see source + target transcripts, per-stage latency, estimated cost, and hear translated audio.
Every component renders **only** from one state store (`UiSessionState`) — so the design is a pure
function of state. The whole system therefore lives or dies on **state visualization**: status pills,
active-mode highlight, enabled/disabled affordances, streaming transcript treatment, latency-vs-target
coloring, qualified cost, and a comparison grid.

---

<!-- CONTENT_FUNDAMENTALS -->
## Content fundamentals

How copy is written across the Workbench and Boostlingo product surfaces.

**Voice — calm, precise, operator-grade.** This is an instrument. Copy states facts, never hypes.
"Estimated $0.18 / min", "speech → first audio", "WER is STT-only". Boostlingo's marketing voice is
warmer ("Communicate without barriers", "Experience the Future of Interpreting"), but **inside the
Workbench** the register is technical and quiet — closer to a lab bench than a landing page.

**Person.** UI labels are **impersonal / imperative** — they name the thing or the action, not the
user: "Start recording", "End session", "Refresh summary", "Record & evaluate". Avoid "you/your" in
controls. Marketing surfaces use "you/your" freely ("Scale your business").

**Casing.** **Sentence case everywhere** — buttons, labels, headings, menu items. "Start recording",
not "Start Recording". Proper nouns keep caps: **Realtime**, **Cascade**, **Boostlingo**, **WER**,
**STT/TTS**, **EN/ES**. Status values render as their literal enum word, lowercase, in a pill:
`idle` `configured` `active` `recording` `processing` `playing` `completed` `failed`.

**Numbers are first-class.** Always qualify and unit them:
- Latency: `1.24 s` / `840 ms` — use `s` above 1000 ms, `ms` below; one or two decimals.
- Cost: **always** prefixed "Estimated" and suffixed `/ min` → "Estimated $0.18 / min". Never a bare
  number; cost is an estimate by definition.
- Quality: `WER 12%` with `S / I / D` (substitutions / insertions / deletions) breakdown.
- **Unavailable values render `n/a`** — never a fake `0`, never blank. Style it as muted, not error.

**Targets are stated inline.** Latency copy references the goal: Cascade end-to-end **< 3 s**,
Realtime speech → first-audio **< 1.5 s**. Color reflects the target (good / warn / over).

**Errors are sanitized + actionable.** Never leak transport/wire detail. Pattern: *what happened* +
*what to do*. "Microphone blocked — allow mic access in your browser, then retry." "Translation
provider failed — retry, or switch to Realtime." Each error has a `code`, a `safeMessage`, optional
`stage`, and `retryable`.

**Microcopy examples (verbatim style):**
- Empty turn: "No turn yet — press Start recording to begin."
- Config gating: "Cascade needs STT, Translation, and TTS configured."
- Cost assumptions (tooltip): "Assumes input-priced audio; output pricing disclosed-unavailable."
- WER note: "WER measures STT accuracy only — not translation quality."

**Emoji:** none in the Workbench UI. (Boostlingo's *social* uses emoji 🤠🍀; the product does not.)
**Tone vibe:** trustworthy, exact, unflashy. Confidence through clarity, not decoration.

---

<!-- VISUAL_FOUNDATIONS -->
## Visual foundations

The Workbench reads as a **clean, friendly instrument panel**: the polished modern-SaaS surface
treatment of the aesthetic reference (soft rounded cards, diffuse shadows, pastel accents, big
numerals) applied to a **data-forward, state-driven** dashboard.

**Color.** Vivid **Boostlingo Blue** (`#2F6BFF`) is the primary — active states, primary buttons,
the Realtime identity. A warm **Coral** (`#FF5C35`, the "Trinidad" accent) is used sparingly for
emphasis / live-record energy. **Cascade** gets a distinct **Violet** (`#6E56F0`) identity so the two
modes are instantly separable in toggles and the comparison grid. Canvas is a very light cool gray
(`#F5F6F8`); cards are white; near-black ink (`#14161B`) doubles as the "dark pill" stat treatment
from the reference. Full semantic scale (success / warn / danger / info) drives latency-vs-target and
status coloring. See `colors_and_type.css` and the Colors cards in the Design System tab.

**Mode identity (important).** Realtime = **blue**, Cascade = **violet**. These two hues carry through
every surface: the mode toggle, status accents, the per-turn metric panels, and especially the
comparison grid (blue column vs violet column). Never swap them.

**Type.** **Plus Jakarta Sans** for all UI and display — a friendly geometric sans that echoes the
Boostlingo wordmark's rounded geometry. **IBM Plex Mono** for every measured number (latency, cost,
WER, IDs, durations) and for the streaming transcript's metadata — tabular, technical, unmistakably
"data". Big bold numerals are a signature: per-turn headline metrics run 40–56px. (Both are Google
Fonts substitutions for Boostlingo's brand font — flagged in CAVEATS.)

**Spacing & layout.** 4px base grid; an 8/12/16/20/24/32px rhythm. The dashboard is a 3-column shell —
**Controls (left rail) · Transcripts (center stage) · Metrics + Cost (right rail)** — with the
**Comparison Summary** as a full-width band below (it gets the most visual weight; it is the
deliverable), then Evaluation, with the Error banner as a top toast / inline strip. Generous gutters
(24–32px), roomy card padding (20–24px).

**Backgrounds.** Flat light-gray canvas. **No** photographic or illustrated backgrounds in the
Workbench, **no** heavy gradients. Pastel tints (sky / lilac / coral-tint / mint / peach) appear only
as soft **fills behind grouped stats or category chips**, exactly as in the reference. A single subtle
brand gradient (blue → violet) is permitted on the brandmark only.

**Cards.** White, **radius 20–24px**, hairline border (`#E9EBF0`) **plus** a soft diffuse shadow
(`0 1px 2px rgba(20,22,27,.04), 0 8px 24px rgba(20,22,27,.06)`). Inner panels / chips use radius
12–14px. "Dark pill" stat chips (near-black bg, white text) are reserved for the single most important
number in a region — used sparingly.

**Borders & dividers.** 1px, `#E9EBF0` (or `#EEF0F4` for very light). Inputs get a 1px border that
shifts to Boostlingo Blue + a 3px focus ring (`rgba(47,107,255,.18)`) on focus.

**Radii.** card 24 · panel 16 · control / button / input 12 · chip / tag 8 · pill / status 999.

**Shadows / elevation.** Three steps: `xs` (1px hairline lift), `sm` (resting card), `md` (raised /
popover / focus overlay). No hard or colored drop shadows except the focus ring.

**Hover states.** Buttons darken ~8% (primary blue → `#1F54E0`). Ghost / secondary controls get a
faint gray fill (`#F1F2F5`). Cards that are interactive lift from `sm` → `md` shadow. Transitions are
quick: **120–160ms ease-out**.

**Press states.** Subtle scale-down (`transform: scale(.98)`) + slightly deeper fill. No bounce.

**Active / selected (the #1 fix).** Selected toggle / segmented options get a **solid filled** treatment
(mode color bg + white text) or a strong ring + tinted fill — never relying on `aria-pressed` alone.
This is the most important single rule: the active mode must be **obvious**.

**Status & live indicators.** Color-coded pills for `sessionStatus` and `turnStatus`. A live
**REC** indicator: pulsing coral dot. `processing` = animated spinner; `playing` = animated audio
bars. Pulse/spin are the only ambient animations.

**Streaming transcript treatment.** Non-final **partials** render dimmed + italic; on finalize they
snap to solid full-color weight. Source and target are two columns (EN | ES). A subtle fade-in on new
text; no sliding.

**Latency presentation.** Per-stage as a small horizontal **segmented bar / timeline** (STT ·
Translation · TTS for Cascade; speech→first-audio headline for Realtime), colored against the target:
**good** (green) under target, **warn** (amber) near it, **over** (red) above. Headline number is big
mono.

**Disabled affordances.** Disabled controls drop to ~40% opacity, lose shadow, `cursor: not-allowed`,
and never show hover. Every button's enabled/disabled state is driven by the gating selectors.

**Transparency & blur.** Used only for: the top error toast (slight backdrop blur over content), the
focus overlay scrim, and tooltip surfaces. Otherwise opaque.

**Animation.** Restrained and functional. Fades + the REC pulse + spinners. Easing `cubic-bezier(.2,
.7, .2, 1)` (ease-out). No parallax, no decorative motion. Respect `prefers-reduced-motion`.

**Imagery vibe.** The Workbench has essentially none. Where Boostlingo brand imagery appears elsewhere
it is warm, human, real photography of people communicating — but that does not enter this tool.

---

<!-- ICONOGRAPHY -->
## Iconography

**System: [Lucide](https://lucide.dev) (CDN).** Boostlingo's own product/web icons are a thin,
rounded, consistent-stroke **gradient line style** (blue→teal/violet) — `human.svg`,
`ai_icon.svg`, `Translation-Solution-Icon_Gradient.svg`, etc. on their CDN. Those marketing icons are
**too decorative for an instrument panel** and I couldn't download them, so the Workbench standardizes
on **Lucide**, whose geometry matches that line look: **1.75px stroke, round caps, round joins, 24px
grid**. This is a flagged substitution — see CAVEATS.

**How to load (CDN):**
```html
<script src="https://unpkg.com/lucide@latest"></script>
<i data-lucide="mic"></i>
<script>lucide.createIcons();</script>
```
Or per-icon SVG from `https://unpkg.com/lucide-static/icons/<name>.svg`.

**Rules.**
- **Stroke:** 1.75px; never fill line-icons. Use `currentColor` so icons inherit text color.
- **Sizes:** 16px (inline w/ 13–14px text), 18px (buttons), 20px (region headers), 24px (empty-state).
- **Color:** match context — `--fg-2` default, mode color when active, semantic color in status.
- **Never** use emoji or unicode glyphs as icons in the Workbench. (Boostlingo *social* uses emoji; the
  product does not.)

**Canonical icon mapping (Workbench):**
| Concept | Lucide icon |
|---|---|
| Realtime mode | `zap` |
| Cascade mode | `git-merge` / `layers` |
| Record / mic | `mic` · muted `mic-off` |
| Stop | `square` |
| Start session | `play` · End `power` / `log-out` |
| Processing | `loader` (spin) |
| Playing audio | `volume-2` / `audio-lines` |
| Latency / timing | `gauge` / `timer` |
| Cost | `dollar-sign` / `receipt` |
| Quality / WER | `target` / `check-check` |
| Provider health | `circle` (filled dot via CSS) · `activity` |
| Direction swap (EN↔ES) | `arrow-left-right` |
| Comparison | `columns-2` / `bar-chart-3` |
| Error | `alert-triangle` · dismiss `x` |
| Info / assumptions | `info` / `circle-help` |
| Settings / models | `sliders-horizontal` |
| Headset hint | `headphones` |

---

## CAVEATS (please help me make this perfect)

1. **Brand assets are recreations, not originals.** I could not download Boostlingo's real SVGs
   (cross-origin). `assets/lockup.svg` and `assets/mark.svg` are clean *placeholders* in the brand's
   style (blue→violet ribbon + lowercase geometric wordmark). **Please drop the real
   `boostlingo-logo.svg` + logo-mark into `assets/`** and I'll wire them in.
2. **Fonts are best-guess substitutions.** Boostlingo's wordmark uses a custom geometric typeface; I
   matched the *feel* with **Plus Jakarta Sans** (UI/display) + **IBM Plex Mono** (data). If you have
   the real brand font (or its name), share it and I'll swap the tokens + add local files to `fonts/`.
3. **Exact brand hexes are inferred.** The blue/violet/coral are derived from the brand's named colors
   ("Blue Ribbon", "Trinidad") + the aesthetic reference — not an official swatch sheet. Send the real
   hex values and I'll correct `colors_and_type.css` in one pass.
4. **Icons substitute Lucide** for Boostlingo's gradient line set (couldn't download those either).
5. **Mode identity colors (Realtime=blue, Cascade=violet) are my proposal** — they make the A/B
   comparison legible. If Boostlingo has canonical per-mode colors, tell me.

---

## Index / manifest

Root files:
- `README.md` — this file (product context, content + visual foundations, iconography, index).
- `UX_SPEC.md` — the verbatim Workbench brief (state machines, selectors, data model, layout, flows).
- `colors_and_type.css` — all design tokens as CSS custom properties (color, type, spacing, radius,
  shadow) + base semantic styles. Import this everywhere.
- `SKILL.md` — Agent-Skills-compatible entry point.

Folders:
- `assets/` — logos / brandmark (recreations, flagged), favicon. See CAVEATS.
- `fonts/` — (Google Fonts are linked via CDN; see `colors_and_type.css`).
- `preview/` — small HTML cards that populate the Design System tab (colors, type, spacing, components).
- `ui_kits/workbench/` — the high-fidelity Workbench UI kit (`index.html` + JSX components). See its
  own README. Interactive: a demo-states bar + a live simulated turn flow.

### Full file manifest
- **Root:** `README.md`, `UX_SPEC.md`, `colors_and_type.css`, `SKILL.md`
- **`assets/`:** `mark.svg`, `lockup.svg` (placeholder brand recreations — see CAVEATS)
- **`preview/`** (Design System tab cards): `colors-*` (brand / neutrals / semantic / status / pastels /
  metric-scale), `type-*` (display / body / mono), `spacing-*` (scale / radii / elevation),
  `comp-*` (buttons / mode-toggle / live-indicators / provider-chips / inputs / latency / cost /
  comparison-grid / transcript / error-banner), `brand-logo`
- **`ui_kits/workbench/`:** `index.html`, `workbench.css`, `store.jsx`, `scenarios.jsx`, `icons.jsx`,
  `components.jsx`, `panels.jsx`, `app.jsx`, `README.md`

(An ICONOGRAPHY deep-dive and a final index live at the bottom of this file.)
