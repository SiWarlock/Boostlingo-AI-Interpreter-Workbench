# 053-C Fixture — Real Realtime Data-Channel Event Capture

**Provenance:** captured live from the browser DEV `[realtime oai-events]` logger
(`realtimeWebRtcClient.ts:141`) during a real OpenAI Realtime turn on the user's key
(2026-05-31). Model `gpt-realtime`, EN→ES, **manual turn control** (`turn_detection: null`).
Source utterance: *"The tests of the real-time mode"* → target: *"Modo en tiempo real activado."*

This is the RED-test fixture the FE impl derives 053-C against (the realtime analogue of the
cascade stage-timeline fixture that grounded 056). Timing for derived metrics is the **client
receipt-stamp** of each frame (the DC frames carry `event_id`, not server timestamps).

---

## ★ Key derivation facts for 053-C

1. **First-audio on the DC = `output_audio_buffer.started`.**
   `response.output_audio.delta` does **NOT** appear — the audio bytes ride the WebRTC media
   track (`pc.ontrack`), not the data channel. The 053-C bug is that first-audio derivation was
   keyed on `response.output_audio.delta` / `playback.started` (never fires) → first-audio and
   everything chained off it stayed `n/a`. **Anchor first-audio on `output_audio_buffer.started`**
   (keep `<audio>` `playback.started` as a fallback). It lands mid-output-transcript-deltas.

2. **Realtime cost IS computable — `response.done` carries full `usage`** (the `n/a` was a
   reporting gap, not missing data): the sink must read `response.done.usage`.
   `usage = { total_tokens: 139, input_tokens: 68 (text 37 / audio 31 / cached 0),
   output_tokens: 71 (text 17 / audio 54) }`.
   The source-transcription `…completed` event also carries its own `usage`
   `{ total 40, input 31 (audio 31), output 9 }`.

3. **Confirmed GA `type` strings** (resolves ARCH-010 §7 smoke-confirm items):
   - Source transcript: `conversation.item.input_audio_transcription.delta` (field `delta`) →
     `.completed` (field `transcript`)
   - Target transcript: `response.output_audio_transcript.delta` (field `delta`) →
     `.done` (field `transcript`)
   - Response terminal: `response.done` (carries `usage`)
   - Audio markers: `output_audio_buffer.started` / `output_audio_buffer.stopped`
   - Turn input commit: `input_audio_buffer.committed`

---

## Full ordered event sequence (one turn)

Obfuscation/`event_id` noise fields trimmed; structurally-relevant fields kept verbatim.

```
1.  session.created        session.audio.input.turn_detection = null;
                           session.audio.input.transcription.model = "gpt-4o-transcribe"
2.  input_audio_buffer.committed     item_id "item_…IIsUqe"
3.  conversation.item.added          role user, content[0] {type:"input_audio", transcript:null}
4.  conversation.item.done           role user (same item)
5.  response.created                 status in_progress; usage null
6.  conversation.item.input_audio_transcription.delta   delta:"The"        ×1
7.  …delta   " tests" / " of" / " the" / " real" / "-time" / " mode"        (7 deltas total)
8.  conversation.item.input_audio_transcription.completed
        transcript:"The tests of the real-time mode"
        usage:{type:"tokens", total_tokens:40, input_tokens:31,
               input_token_details:{text_tokens:0, audio_tokens:31}, output_tokens:9}
9.  response.output_item.added       assistant message, status in_progress
10. conversation.item.added          assistant (in_progress)
11. response.content_part.added      part {type:"audio", transcript:""}
12. response.output_audio_transcript.delta   delta:"Modo" / " en" / " tiempo"   (3 deltas)
13. ★ output_audio_buffer.started    response_id "resp_…lDwVPTh6mDFoJlOH"   ← FIRST-AUDIO ANCHOR
14. response.output_audio_transcript.delta   delta:" real" / " activ" / "ado" / "."  (4 deltas)
15. response.output_audio.done
16. response.output_audio_transcript.done    transcript:"Modo en tiempo real activado."
17. response.content_part.done       part {type:"audio", transcript:"Modo en tiempo real activado."}
18. conversation.item.done           assistant, content[0] {type:"output_audio", transcript:"Modo en tiempo real activado."}
19. response.output_item.done        assistant (completed)
20. ★ response.done                  status completed;
        usage:{ total_tokens:139,
                input_tokens:68,  input_token_details:{text_tokens:37, audio_tokens:31, image_tokens:0, cached_tokens:0,
                                    cached_tokens_details:{text_tokens:0, audio_tokens:0, image_tokens:0}},
                output_tokens:71, output_token_details:{text_tokens:17, audio_tokens:54} }
21. rate_limits.updated              tokens: limit 40000, remaining 39360, reset 0.96s
22. output_audio_buffer.stopped      response_id "resp_…lDwVPTh6mDFoJlOH"
```

**Note (Phase-I relevance):** `session.created` confirms `turn_detection: null` (manual mode) —
so transcription + response only fire after the Stop-driven `input_audio_buffer.committed`. This
is why realtime shows no live/streaming output until Stop (expected pre-Phase-I; Phase-I server-VAD
auto-commits speech segments → streaming parity with cascade).
