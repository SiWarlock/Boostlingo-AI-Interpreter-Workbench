# Realtime vs Cascade — Comparison Write-up

> **STATUS: Phase G.5 — all numbers filled from the 2026-06-01 real-key runs.** Latency, WER, stability, and error figures are from the 5-min × both-modes soak (`docs/soak-runs/2026-06-01-{cascade,realtime}.json`). **Realtime cost is the corrected (post-094/095-fix) figure** from a fresh re-run (`docs/soak-runs/2026-06-01-realtime-corrected.json` / `data/sessions/…cd7b9e5a.json`): the initial soak over-counted realtime cached-audio (it billed cached input audio at the full input rate instead of the 80×-cheaper cached rate); fixed in **094** (BE re-pricing) + **095** (FE forwards the cached-audio subset `cached_tokens_details.audio_tokens`), live-confirmed (cached now 320 tokens/turn priced at the cached rate). Realtime cost is from the corrected re-run; latency/WER/stability are from the initial run (the cost fix is pricing-only — it does not affect those). All numbers from `data/sessions/<id>.json` + the SoakReports — never hand-estimated.

---

## 1. What was built

An instrumented comparison workbench (not a production interpreter) that runs two interpretation modes behind one UI and captures normalized evidence for each turn:

- **Realtime** — OpenAI `gpt-realtime`/`-mini` over browser WebRTC, authorized by a backend-minted ephemeral credential.
- **Cascade** — a fully streaming Deepgram `nova-3` STT → OpenAI `gpt-5-nano`/`-mini` translation → OpenAI `gpt-4o-mini-tts` pipeline, each stage behind a swappable provider interface.

Evidence captured per turn: a normalized `LatencyEvent` timeline (stamped on real provider-event arrival), a config-driven cost estimate, optional WER (STT quality vs scripted phrases), normalized errors — persisted to local JSON (no raw audio, no secrets). A Comparison view aggregates this **by mode and by model variant**.

## 2. Measurement method + limitations

**How latency is measured.** Each `LatencyEvent` is stamped on the *first real arrival* of its event type from the provider stream (`stt.first_partial`, `translation.first_token`, `tts.first_audio`, realtime `first_audio`) — never synthesized. Aggregates are computed from **absolute timestamps** (cross-clock-safe); the per-event `relativeMs` is display-only and never used for cross-event math. A missing endpoint is reported **`n/a`**, never `0`.

**Limitations to weigh when reading the numbers:**

- **Cross-clock skew.** Some timings cross the browser↔server clock boundary; skew is *disclosed, not silently clamped*. Treat sub-~50ms differences as noise.
- **Backend-measured TTS timing.** Cascade stage latencies are measured server-side at the provider stream; they exclude the final network hop to the browser's speaker.
- **Cascade end-to-end latency (speechEnd→playback) is `n/a`.** The cascade transport has no client→server latency-report channel, so the cross-mode *end-to-end* latency comparison uses the realtime end-to-end timing + the cascade *per-stage* latencies (not a single cascade speechEnd→playback number). _(A documented architectural limitation, not a bug.)_
- **Cost is an estimate, not a bill.** Figures come from a config-driven `pricing.json` × measured usage — exact `response.done.usage` audio-token counts for realtime, per-stage tokens/audio-minutes for cascade. Valid for *relative* comparison; not provider invoices. **Realtime cost grows within a session** — the model re-bills the accumulating conversation context as input on every turn (partially offset by prompt caching), so per-turn cost climbs with conversation length (§3.2). **Cascade is stateless and flat per turn.** The initial soak surfaced a realtime *cached-audio over-count* (cached input audio was billed at the full input rate instead of the 80×-cheaper cached rate) → corrected in briefs 094 (BE re-pricing) + 095 (FE forwards the cached-audio subset); the realtime $/min below is the **corrected** figure. 094's unit tests pin the exact pricing; the live re-run corroborated it (corrected total $0.385 vs the initial $0.585 ≈ 1.5× lower) — but the two runs are *not* a controlled A/B (different conversations → different cached-token amounts), so treat the live ratio as corroboration, not proof.
- **Realtime latency is browser-clock + timestamp-derived; cascade per-stage is server-clock.** Realtime aggregates are computed from per-turn absolute browser timestamps (`recording.stopped → realtime.first_audio_delta`) because the realtime session-summary excluded turns persisted with zero transcripts (the empty-silence guard) — the per-turn timestamps are intact. The cross-mode latency comparison thus crosses clocks (disclosed, not clamped), and the realtime number *includes* the network hop to the client while the server-measured cascade number *excludes* the final hop to the speaker — a small bias in cascade's favor.
- **WER is measured on synthetic speech.** The soak injects OpenAI-TTS-generated audio (not human speech), so absolute WER values are directional; the realtime-vs-cascade WER gap reflects how each path's STT handles this particular synthetic audio, not a human-speech quality verdict.
- **Output-overlap (drift) basis is asymmetric.** Realtime overlap is token-derived (precise); cascade overlap is a character-estimate (rougher) — not directly comparable in absolute terms, though both pass the no-drift threshold.
- **English-leaning ES TTS voice.** The Spanish output uses an OpenAI voice that is English-leaning; quality is observed, not scored (WER measures STT, not TTS).
- **Turn counts are exact** — standalone WER-evaluation turns are excluded from the per-mode comparison counts (F.4); the comparison reflects interpretation turns only.

## 3. Results

> Measured from the 2026-06-01 5-min × both-modes real-key soak (cascade 24 interpretation turns / realtime 25). Values from the persisted session JSON + the SoakReports.

### 3.1 Latency (avg, by mode)

| Metric | Realtime | Cascade |
|---|---|---|
| **Speech-end → first audio** (responsiveness) | **669 ms** (mean; range 310–1328, n=23) | **1914 ms** |
| STT (turn-start → final) | n/a (single hop) | ~6216 ms \* |
| Translation (start → final) | n/a (single hop) | 611 ms |
| TTS (start → first audio) | n/a (folded into the 669 ms above) | 1000 ms |

\* Cascade STT-final is measured from turn start and is dominated by the synthetic utterance duration (~4 s) + Deepgram endpointing — **not** a like-for-like compute latency. The meaningful cascade per-stage latencies are translation (611 ms) and TTS first-audio (1000 ms); their sum + inter-stage gaps reconciles to the ~1914 ms responsiveness headline.

**Takeaway:** realtime is **~2.9× faster on responsiveness** (single-hop speech-to-speech) — its core latency advantage. Cascade's added latency is the price of per-stage observability + provider swappability. (Clocks differ — see §2; the bias is slightly in cascade's favor since its server-measured number excludes the final hop to the speaker.)

### 3.2 Cost (estimated $/min, by mode AND model variant)

| Mode | Model variant | Est. $/min |
|---|---|---|
| Realtime | `gpt-realtime` | **~$0.24/min** (corrected build; per-turn $0.006→$0.029, climbing — see below) |
| Realtime | `gpt-realtime-mini` | not measured this run |
| Cascade | `gpt-5-nano` (translation) | **$0.012/min** (flat; ~$0.0019/turn) |
| Cascade | `gpt-5-mini` (translation) | not measured this run |

**Headline: realtime is ~20× cascade per minute on this run (~$0.24 vs ~$0.012/min) — and the gap widens with session length** (realtime climbs, cascade is flat). The ~20× is the corrected multiple; the over-counted initial figure had implied ~27×.

**⭐ Cost structure differs fundamentally — not just in magnitude.** Cascade is **stateless**: each turn is an independent STT→translate→TTS, so per-turn cost is flat (~$0.0019/turn, ~$0.012/min) regardless of conversation length. Realtime is **stateful**: each turn re-bills the *entire accumulating conversation context* as input audio (partially offset by prompt caching), so per-turn cost **climbs through a session** — measured **$0.006 → $0.029/turn** over 5 minutes as the per-turn `audioInputTokens` grew **39 → 1126** (output tokens stayed flat; figures from the corrected post-094/095 re-run). For long interpretation sessions this compounding is the dominant realtime cost driver, and the single most important cost finding for the Boostlingo use case. _(The **climbing-vs-flat structure** is the robust finding; the absolute $/min is the corrected post-fix figure.)_

### 3.3 Quality (WER, STT-only) + errors

| | Realtime | Cascade |
|---|---|---|
| WER (mean, n) | 0.174 (n=23) | 0.025 (n=24) |
| WER (median) | 0 | 0 |
| Error count | 0 | 0 |
| Turn count | 25 | 24 |

**Takeaway:** on this synthetic audio, cascade's Deepgram `nova-3` STT transcribed markedly more accurately (mean WER 0.025 vs 0.174). Both medians are 0 — most turns transcribed perfectly in both modes; realtime's higher *mean* is pulled up by a minority of high-error turns. With the synthetic-audio caveat (§2) this is a directional STT-quality data point, not a human-speech verdict. Zero errors in both modes over the 5-minute run.

### 3.4 Controllability (qualitative)

- **Realtime:** Conversational, low-latency turn-taking. **Interrupts on barge-in** — when the next utterance begins before the response finishes, the in-flight response is cancelled (`response.done status:"cancelled", reason:"turn_detected"`; 2 such turns observed in the soak, handled cleanly with no error). Single-model steerability (one instruction prompt sets interpreter behavior). Trade-off: **no per-stage observability** — STT/translation/TTS are opaque inside one model, so a quality or cost anomaly can't be isolated to a stage, and the language pair is a fixed model capability (§5).
- **Cascade:** **Full per-stage observability** — STT, translation, and TTS are each instrumented and independently swappable behind a provider abstraction; a failure or cost anomaly is isolated to its stage, and a stage's provider/model/voice swaps without touching the others. **Does NOT interrupt on barge-in** — it completes the in-flight turn (a UX difference, not a defect). Trade-off: more end-to-end latency (the pipeline hops) + an English-leaning ES voice.

### 3.5 Stability (5-min soak, ARCH-020)

Both modes ran a scripted 5-minute bidirectional EN↔ES conversation through the real pipeline. All three ARCH-020 stability invariants pass in both modes:

| Stability metric | Realtime | Cascade | Threshold |
|---|---|---|---|
| Latency drift (slope) | 37.76 ms/turn ✅ | 3.48 ms/turn ✅ | < 50 ms/turn |
| Heap growth (slope) | 2378 B/sample ✅ | 6968 B/sample ✅ | < 200 000 B/sample |
| Transport disconnects | 0 ✅ | 0 ✅ | 0 |
| `noDisconnect` / `noDriftOverlap` / `noLeak` | all true | all true | all true |

Neither mode degraded, leaked, or drifted over the run. Cascade shows a flatter latency slope (3.48 vs 37.76 ms/turn) consistent with its stateless per-turn model; realtime's gentle positive slope tracks the growing conversation context (the same property behind its climbing cost), but stays well under threshold.

## 4. Recommendation — when each fits

- **Choose Realtime when** responsiveness + conversational feel dominate — ~669 ms speech-to-first-audio vs cascade's ~1914 ms (§3.1), a single-model integration, and natural barge-in interruption (§3.4). Accept: **no per-stage observability**, a **session cost that compounds with conversation length** (§3.2), higher STT error on this audio (§3.3), and a language pair fixed to the model's capability (§5).
- **Choose Cascade when** cost predictability, per-stage observability, provider flexibility, and language-pair breadth dominate — flat ~$0.012/min regardless of session length (§3.2), each stage instrumented + swappable (§3.4), and markedly lower STT error here (§3.3). Accept: ~2.9× more speech-to-first-audio latency (§3.1), and barge-in completes the turn rather than interrupting.

## 5. Time-to-onboard a new language pair (PRD impact metric)

The deciding operational metric for the Boostlingo use case — how much work is a new language pair?

- **Cascade:** a **config / provider-capability change** — point STT (`DEEPGRAM_STT_LANGUAGE`) + the translation prompt/direction + a TTS voice at the new pair; no model retraining, bounded by provider language coverage. EN↔ES ran off-the-shelf in the soak (Deepgram `nova-3 multi` + the translation prompt + an ES voice); for a new pair the usual constraint is TTS voice availability + STT language coverage — both provider-config changes, and the weakest stage can be swapped independently.
- **Realtime:** a **model-capability question** — the pair must be supported by the realtime model itself; there's no per-stage knob to swap. EN↔ES is within `gpt-realtime`'s range (confirmed in the soak); a new pair lives or dies by that single model's coverage.
- **Implication:** for language-pair breadth, **Cascade's swappable stages give more reach and a graceful degrade path** (swap the constraining stage's provider) at the cost of more integration surface; **Realtime is simpler to integrate but bounded by one model's coverage**. For the Boostlingo use case (breadth of pairs matters), this favors Cascade's architecture for coverage, with Realtime reserved for high-value pairs where its responsiveness wins.

## 6. Limitations + next steps

- Numbers are from the 2026-06-01 soak; they shift with provider/model/pricing changes — re-run to refresh.
- **Resolved this round:** the realtime cached-audio cost over-count — fixed (094 BE re-pricing + 095 FE cached-audio-subset forwarding, unit-test-pinned) and live-confirmed via a corrected re-run; §3.2's realtime cost is now the corrected figure (~$0.24/min).
- Next steps (tracked in `MVP_TASKS.md`): add a cascade client→server latency channel (a true end-to-end cascade speechEnd→playback number — today `n/a`); persist realtime per-turn transcripts so the realtime session-summary aggregates populate (the empty-silence guard currently excludes zero-transcript turns); the deferred hardening items (auth gate, model allowlist, bounded growth).
