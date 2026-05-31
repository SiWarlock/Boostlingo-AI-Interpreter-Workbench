# Realtime vs Cascade — Comparison Write-up

> **STATUS: SCAFFOLD (Phase G.5).** The structure, methodology, and limitations below are final. Every **`[SMOKE: …]`** marker is a placeholder for a **real measured value** that must be filled from actual session JSON after a real-key smoke run (running both modes with provider keys, per `docs/DEMO_SCRIPT.md`). **Do not present this as final until the `[SMOKE: …]` markers are replaced with measured numbers.** Source the numbers from `data/sessions/<id>.json` + the in-app Comparison panel — never estimate them by hand.

---

## 1. What was built

An instrumented comparison workbench (not a production interpreter) that runs two interpretation modes behind one UI and captures normalized evidence for each turn:

- **Realtime** — OpenAI `gpt-realtime`/`-mini` over browser WebRTC, authorized by a backend-minted ephemeral credential.
- **Cascade** — a fully streaming Deepgram `nova-3` STT → OpenAI `gpt-5.4-nano`/`-mini` translation → OpenAI `gpt-4o-mini-tts` pipeline, each stage behind a swappable provider interface.

Evidence captured per turn: a normalized `LatencyEvent` timeline (stamped on real provider-event arrival), a config-driven cost estimate, optional WER (STT quality vs scripted phrases), normalized errors — persisted to local JSON (no raw audio, no secrets). A Comparison view aggregates this **by mode and by model variant**.

## 2. Measurement method + limitations

**How latency is measured.** Each `LatencyEvent` is stamped on the *first real arrival* of its event type from the provider stream (`stt.first_partial`, `translation.first_token`, `tts.first_audio`, realtime `first_audio`) — never synthesized. Aggregates are computed from **absolute timestamps** (cross-clock-safe); the per-event `relativeMs` is display-only and never used for cross-event math. A missing endpoint is reported **`n/a`**, never `0`.

**Limitations to weigh when reading the numbers:**

- **Cross-clock skew.** Some timings cross the browser↔server clock boundary; skew is *disclosed, not silently clamped*. Treat sub-~50ms differences as noise.
- **Backend-measured TTS timing.** Cascade stage latencies are measured server-side at the provider stream; they exclude the final network hop to the browser's speaker.
- **Cascade end-to-end latency (speechEnd→playback) is `n/a`.** The cascade transport has no client→server latency-report channel, so the cross-mode *end-to-end* latency comparison uses the realtime end-to-end timing + the cascade *per-stage* latencies (not a single cascade speechEnd→playback number). _(A documented architectural limitation, not a bug.)_
- **Cost is an estimate, not a bill.** Figures come from a config-driven `pricing.json` × measured usage. They are valid for *relative* comparison; they are not provider invoices. Realtime currently prices **input audio**; the realtime *output*-audio duration is disclosed-but-not-yet-measured frontend-side (so realtime cost is a slight under-count, flagged in the estimate's assumptions). Translation `gpt-5.4-mini` rates may still be placeholder `0.0` pending a pricing re-verify.
- **English-leaning ES TTS voice.** The Spanish output uses an OpenAI voice that is English-leaning; quality is observed, not scored (WER measures STT, not TTS).
- **Turn counts are exact** — standalone WER-evaluation turns are excluded from the per-mode comparison counts (F.4); the comparison reflects interpretation turns only.

## 3. Results

> Fill from a real-key smoke run (≥2 turns per mode, both translation models, ≥1 WER phrase). All values from session JSON / the Comparison panel.

### 3.1 Latency (avg, by mode)

| Metric | Realtime | Cascade |
|---|---|---|
| Speech-end → first audio | `[SMOKE: ms]` | `[SMOKE: ms or n/a]` |
| STT first-partial / final | `n/a (single hop)` | `[SMOKE: ms]` |
| Translation first-token / final | `[SMOKE: ms or n/a]` | `[SMOKE: ms]` |
| TTS first-audio | `[SMOKE: ms]` | `[SMOKE: ms]` |

### 3.2 Cost (estimated $/min, by mode AND model variant)

| Mode | Model variant | Est. $/min |
|---|---|---|
| Realtime | `gpt-realtime` | `[SMOKE: $]` |
| Realtime | `gpt-realtime-mini` | `[SMOKE: $]` |
| Cascade | `gpt-5.4-nano` (translation) | `[SMOKE: $]` |
| Cascade | `gpt-5.4-mini` (translation) | `[SMOKE: $]` |

### 3.3 Quality (WER, STT-only) + errors

| | Realtime | Cascade |
|---|---|---|
| WER (avg, sample count) | `[SMOKE: % (n)]` | `[SMOKE: % (n)]` |
| Error count | `[SMOKE: n]` | `[SMOKE: n]` |
| Turn count | `[SMOKE: n]` | `[SMOKE: n]` |

### 3.4 Controllability (qualitative)

- **Realtime:** `[SMOKE/OBSERVE: turn-taking feel, interruption handling, manual VAD-off control, model steerability]`
- **Cascade:** `[SMOKE/OBSERVE: per-stage observability, provider swappability, mid-pipeline control, failure isolation]`

## 4. Recommendation — when each fits

> Anchor each claim to a §3 number once filled.

- **Choose Realtime when** `[lower end-to-end latency / simpler integration / conversational feel — cite §3.1]`, accepting `[less per-stage observability + the realtime cost profile — cite §3.2]`.
- **Choose Cascade when** `[per-stage observability / provider flexibility / cost control / language coverage via STT — cite §3.2/§3.3]`, accepting `[the additional pipeline latency — cite §3.1]`.

## 5. Time-to-onboard a new language pair (PRD impact metric)

The deciding operational metric for the Boostlingo use case — how much work is a new language pair?

- **Cascade:** a **config / provider-capability change** — point STT (`DEEPGRAM_STT_LANGUAGE`) + the translation prompt/direction + a TTS voice at the new pair; no model retraining, bounded by provider language coverage. `[OBSERVE: which stage is the constraint for the target pair]`
- **Realtime:** a **model-capability question** — the pair must be supported by the realtime model itself; there's no per-stage knob to swap. `[OBSERVE: realtime model's coverage for the target pair]`
- **Implication:** `[the architecture recommendation for language-pair breadth — Cascade's swappable stages vs Realtime's single-model dependency]`

## 6. Limitations + next steps

- Real-provider numbers depend on the smoke run (this scaffold's `[SMOKE: …]` markers); the figures will shift with provider/model/pricing changes — re-run to refresh.
- Next steps (tracked in `MVP_TASKS.md`): measure realtime output-audio duration (cost accuracy); add a cascade client→server latency channel (end-to-end cascade latency); re-verify `pricing.json` (esp. `gpt-5.4-mini` + realtime token factors); the deferred hardening items (auth gate, model allowlist, bounded growth).
