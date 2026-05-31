# Claude Design Prompt — AI Interpreter Workbench

> Copy everything in the box below into Claude Design. It carries the product context, the layout, the dynamic states, and a clear visual direction. Pair it with `ui-design-spec.md` if Claude Design accepts a second reference (that file has the field-level component/state detail).

---

```
Design a polished, clean, intuitive single-screen dashboard for an "AI Interpreter Workbench."

# What it is
The AI Interpreter Workbench is a developer/evaluation tool that runs live speech
interpretation (translating spoken language in real time) using TWO different AI
architectures, side by side, and measures them so an engineer can compare which is
better. It is NOT a consumer translation app — it is an instrumented comparison
workbench. The product IS the measured evidence: latency, cost, and quality.

The two architectures being compared (make each visually distinct and consistent
everywhere it appears — a color and/or icon identity):
  • REALTIME  — one integrated model (OpenAI Realtime) does speech→translation→speech
                in a single live stream. Fast, fewer moving parts.
  • CASCADE   — a pipeline of three specialized models: Speech-to-Text → Translation →
                Text-to-Speech. More controllable, more visible per-stage timing.

# Who uses it
A single technical operator (an engineer evaluating the two approaches). They sit at
this one screen, configure a session, speak short phrases into a mic, and watch the
results stream in. They care about, at a glance: which mode + state they're in, the
per-stage latency as it arrives, the estimated cost, and the head-to-head comparison.
Language pair is English ↔ Spanish, both directions.

# The core interaction: a "turn"
Press Start → speak a phrase → press Stop. Then: the spoken text appears (source), the
translation appears (target), per-stage timing fills in, an estimated cost shows, and
the translated audio plays. The user runs several turns, switches modes between turns,
and compares.

# Design objective
A polished, modern, professional engineering/analytics dashboard — calm, data-forward,
and immediately legible. Think the clarity and restraint of Linear, Vercel's dashboard,
or a well-designed observability tool (Grafana/Datadog but cleaner and less busy).
Easy to use, intuitive, well-organized. The numbers are the hero — typography and layout
should make latency/cost/quality effortless to read and compare. NOT playful, NOT
consumer, NOT cluttered.

# Single-page dashboard layout
One screen, no routing or multiple pages. Suggested information architecture (operator
eye-path: configure → speak → read → compare):

  ┌─ HEADER ────────────────────────────────────────────────────────────────┐
  │ Product title · prominent SESSION-STATUS indicator · provider-health     │
  │ chips (Realtime / STT / Translation / TTS — ready vs unavailable)        │
  ├─ LEFT: CONTROLS ──┬─ CENTER: LIVE STAGE ───────┬─ RIGHT: METRICS ────────┤
  │ • Mode selector   │ Source + Target transcripts │ This-turn latency:      │
  │   (Realtime|Cascade│ side by side, streaming in  │  speech→first-audio,    │
  │    — clear active) │ live (partial → finalized)  │  per-stage (STT/Trans/  │
  │ • Session setup    │                             │  TTS), total            │
  │   (label, direction│                             │ Session averages        │
  │    model pickers,  │                             │ ── Cost ──              │
  │    Start/End)      │                             │ Estimated $X.XX/min +   │
  │ • Recording        │                             │ model + assumptions     │
  │   (Start ● / Stop ■)│                            │                         │
  ├───────────────────┴─────────────────────────────┴─────────────────────────┤
  │ COMPARISON SUMMARY  ← the centerpiece / deliverable                        │
  │ Realtime vs Cascade, by mode AND by model variant:                        │
  │ avg latency · cost/min (4 variants) · error counts · WER quality · turns  │
  ├────────────────────────────────────────────────────────────────────────────┤
  │ EVALUATION (WER quality): pick a scripted phrase → record → WER score      │
  ├────────────────────────────────────────────────────────────────────────────┤
  │ ERROR / NOTICE area (sanitized, actionable)                                │
  └────────────────────────────────────────────────────────────────────────────┘

Give the COMPARISON SUMMARY real visual weight — it's what the whole tool exists to
produce.

# The dynamic states to design (this is a LIVE tool — design the states, not just the empty shell)
1. SESSION STATUS — a prominent, color-coded indicator: idle → configured → starting →
   active → ended. The user must always know where they are.
2. TURN STATUS — an unmistakable live indicator: ready → recording → processing →
   playing → completed (or failed). RECORDING especially needs obvious feedback
   (pulse, red dot, "REC"). During recording/processing/playing the mode selector is
   LOCKED (show it disabled).
3. STREAMING TRANSCRIPTS — in-progress "partial" text must look different from
   "finalized" text (e.g. dimmed/italic → solid). Source and target update independently.
4. METRICS FILLING IN LIVE — values arrive progressively; show "n/a" cleanly where a
   value isn't available (never a blank or a fake 0).
5. LATENCY vs TARGETS — color-code against goals: Cascade end-to-end < 3s, Realtime
   speech→first-audio < 1.5s. Green = meets, amber = close, red = over.
6. ENABLED/DISABLED CONTROLS — Start session, Start/Stop recording, and the mode
   selector each enable/disable based on session + turn state. Make the affordance clear.
7. ERRORS — clean, sanitized, actionable banners (e.g. "mic access blocked — re-allow",
   "switch to Cascade"). Calm, not alarming.
8. LOADING + EMPTY states for each panel (config loading, no-turn-yet, no-data-yet).

# Components to design (11)
Header/title · Session-status indicator · Provider-health chips · Mode selector ·
Session setup (label + direction + 2 model pickers + Start/End) · Recording controls ·
Transcript panel (source + target, streaming) · Metrics panel (per-turn + per-stage +
session averages) · Cost panel · Evaluation/WER panel · Comparison summary · Error banner.

# Visual system
• Aesthetic: clean, modern, professional. Calm neutral base (a refined light theme OR a
  high-contrast dark theme — your call, pick one and commit). Generous but efficient
  spacing; data-dense without clutter; strong, clear typographic hierarchy.
• Color: a neutral foundation + ONE distinct hue each for Realtime and Cascade (so the
  comparison reads instantly anywhere) + semantic green/amber/red for metric-vs-target +
  a muted treatment for "n/a"/disabled.
• Typography: a clean sans for UI; use TABULAR / monospaced numerals for all metrics,
  latency, and cost so columns align and numbers are scannable.
• Motion: subtle and purposeful only — streaming transcript, a recording pulse, a gentle
  metric count-up. No decorative animation.

# UX priorities (in order)
1. At a glance: "what mode am I in, and what state is the session/turn in?"
2. Unmistakable recording / processing / playing feedback.
3. Latency + cost instantly readable AND comparable.
4. The comparison view as the clear centerpiece.
5. An intuitive single-session flow: configure → record → read → compare → repeat.

# Constraints
• Single page; no navigation/routing.
• It restyles + rearranges an existing working app — visual/layout only, the data and
  behavior already exist. Keep the 11 component regions as distinct areas.
• Please design the KEY states explicitly: (a) fresh/idle, (b) mid-turn recording,
  (c) mid-turn streaming with transcripts + partial metrics, (d) turn completed with full
  metrics + cost, (e) comparison populated with data in both modes, (f) an error state.

# Deliverable
A polished dashboard design: the default active layout plus the key dynamic states above,
with a coherent, documented visual system (color tokens, type scale, spacing, component
styles). Optimize for clarity, intuitiveness, and a confident professional feel.
```

---

## How to use this

1. Paste the boxed prompt into Claude Design.
2. If it accepts a second reference, attach `ui-design-spec.md` (field-level component + state detail).
3. Iterate on the result; when you're happy, share the output back here (screenshots, HTML/CSS, design tokens, or a description) and we'll implement it as **Phase H / H.1** against the real components — CSS/layout only, the behavior already works.

**Why this prompt is shaped this way:** it leads with *what the product is and who uses it* (so the design serves the real job), names the **two modes as first-class visual entities** (the comparison is the point), and — most importantly — tells Claude Design to design the **dynamic states**, not just a static empty shell, because this is a live instrument where the recording/streaming/metric states ARE the experience.
