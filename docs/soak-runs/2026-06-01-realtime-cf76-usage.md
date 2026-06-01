# CF76 — live realtime `response.done.usage` wire-shape evidence (2026-06-01 soak run)

Captured by the lead from the live GA realtime soak run (`gpt-realtime`, model `gpt-realtime`,
session `sess_Dly8ZLfbbMFRfvxeKJYKF`) via the browser `[realtime oai-events]` DC logger
(`web/src/realtime/realtimeWebRtcClient.ts:68`). This is durable evidence so the ~7-min real-key
run does **not** need to be repeated for the CF76 parser check.

## The canonical completed-turn frame (turn 1)

```json
{"type":"response.done","response":{"object":"realtime.response","status":"completed",
  "usage":{
    "total_tokens":188,"input_tokens":90,"output_tokens":98,
    "input_token_details":{"text_tokens":51,"audio_tokens":39,"image_tokens":0,
      "cached_tokens":0,"cached_tokens_details":{"text_tokens":0,"audio_tokens":0,"image_tokens":0}},
    "output_token_details":{"text_tokens":24,"audio_tokens":74}
  }}}
```

**Field path for realtime output-audio tokens:** `response.done` → `response.usage.output_token_details.audio_tokens`.
The shape was **identical across all 25 turns** (only the counts vary).

## CF76 verification owed to orch/FE

Verify `extractRealtimeUsage` (`web/src/realtime/realtimeEvents.ts`) reads exactly
`usage.output_token_details.audio_tokens` (+ `text_tokens`) and `usage.input_token_details.*`.
If it already does (per 053-C2b token-pricing), CF76 is a **no-op** and realtime cost is correct.

**One substantive nuance — `cached_tokens`:** many turns carry non-zero `cached_tokens` (e.g. 512)
with a `cached_tokens_details` audio/text breakdown nested under `input_token_details`, e.g. turn @9:44:04:
```json
"input_token_details":{"text_tokens":247,"audio_tokens":498,"cached_tokens":512,
  "cached_tokens_details":{"text_tokens":192,"audio_tokens":320}}
```
If the realtime cost model prices all input tokens at full rate and ignores the cached discount,
it slightly **over**-counts input cost. Flagging for the orch to decide whether that precision
matters for G.5 (likely minor; honest to disclose either way).

## Barge-in / interrupt behavior (honest-degrade note for G.5)

Several `response.done` frames are `status:"cancelled"`, `status_details.reason:"turn_detected"`,
with all-zero usage — the realtime path INTERRUPTS the in-flight response when the next utterance
starts (cascade does not). The soak harness handled these cleanly (final turnCount 25, all three
ARCH-020 booleans pass). This is a UX/behavior difference to report in G.5, not a defect.

## Provenance
- Run: realtime soak, `?soak=1`, 2026-06-01, duration 300s, 25 turns, 0 disconnects.
- Report: `docs/soak-runs/2026-06-01-realtime.json`. Cascade counterpart: `docs/soak-runs/2026-06-01-cascade.json`.
- Note: at run-finalization the harness re-emits the buffered event log (duplicate event_ids appear
  in console ~5 min in) — a logging artifact of report computation, not a re-run or a disconnect.
