# CLAUDE_CODE_HANDOFF.md — AI Interpreter Workbench

## Goal

Review the attached architecture package, identify gaps, finalize `ARCHITECTURE.md`, and only then create `MVP_TASKS.md` from the user's provided template.

Do not start implementation until the architecture gap audit is complete and confirmed.

---

## Inputs

Read these documents end-to-end:

1. Boostlingo PRD.
2. `PRESEARCH.md`.
3. `RESEARCH.md`.
4. `DECISIONS.md`.
5. `ARCHITECTURE.md`.
6. `DIAGRAM_PLAN.md`.
7. User's `MVP_TASKS.md` template, when provided.

---

## Architecture Baseline

The current locked baseline is:

- Frontend: TypeScript SPA.
- Backend: .NET/C# API/orchestrator.
- Realtime: OpenAI Realtime via browser WebRTC + backend-created ephemeral credential.
- Cascade: Deepgram STT → OpenAI translation → OpenAI TTS.
- UX: click start/stop turn-based recording.
- Languages: explicit English → Spanish and Spanish → English.
- Persistence: local JSON session files only.
- No raw audio persistence.
- Metrics: shared latency event schema.
- Cost: live config-driven estimated cost/minute.
- WER: backend scripted WER utility for STT quality.
- Provider scope: one real provider per stage plus interfaces and fakes.
- Deployment: local-first; optional AWS only after local stability.

---

## Required Gap Audit

Before implementation, audit `ARCHITECTURE.md` for:

1. Missing user flows.
2. Missing lifecycle states.
3. Missing API contracts.
4. Missing data model fields.
5. Missing provider interface details.
6. Unclear source-of-truth ownership.
7. Current API drift in OpenAI Realtime, OpenAI TTS, or Deepgram SDK.
8. Browser audio format/transcoding assumptions.
9. Missing error/failure paths.
10. Missing security boundaries.
11. Missing tests.
12. Scope creep relative to 15–20 hour timebox.
13. Missing anchors needed for task planning.

Return findings grouped as:

- Critical gaps.
- Important gaps.
- Nice-to-have improvements.
- Proposed architecture edits.
- Questions requiring human decision.

---

## Rules for Finalizing Architecture

1. Do not change load-bearing decisions without human confirmation.
2. Do not introduce a database unless explicitly approved.
3. Do not persist raw audio unless explicitly approved.
4. Do not put provider API keys in frontend code.
5. Do not remove provider abstractions.
6. Do not remove fake providers/tests.
7. Do not silently downgrade both modes into non-streaming final-only demos.
8. If a provider's current API makes a planned approach impossible, flag it and propose a minimal fallback.

---

## Rules for Creating `MVP_TASKS.md`

Only create `MVP_TASKS.md` after the architecture is finalized.

Every task must:

- Reference one or more architecture anchors, e.g. `ARCH-010`, `ARCH-012`.
- Include acceptance criteria.
- Include test expectations where applicable.
- Avoid inventing architecture not present in `ARCHITECTURE.md`.
- Preserve local-first build order.
- Prioritize backend seams and tests before UI polish.

Recommended implementation order:

1. Repo scaffold/config/docs.
2. Domain/session/metrics models.
3. Provider interfaces and fakes.
4. Cascade orchestrator tests.
5. WER/cost/persistence tests.
6. Real cascade providers.
7. Frontend shell/session controls.
8. Audio capture/playback.
9. Cascade UI integration.
10. Realtime token broker and WebRTC client.
11. Metrics/cost/WER panels.
12. Comparison summary.
13. README/demo script/write-up.

---

## Preflight Questions to Resolve Before Build

1. What exact frontend framework will be used? Recommendation: React + Vite.
2. What exact OpenAI Realtime model is available in the account? Keep configurable.
3. What exact OpenAI TTS model/voice should be used? Keep configurable.
4. What exact Deepgram model should be used? Recommendation: Nova-3 Multilingual or current best low-latency multilingual option.
5. What browser audio format will be sent to cascade endpoint?
6. Is server-side transcoding needed or can provider accept the captured format?
7. Will Realtime transcripts be available with selected model/session config?
8. What current pricing values should be placed into `pricing.json`?

---

## Success Condition

The implementation plan is ready when Claude Code can create `MVP_TASKS.md` with no hidden chat context and no ambiguous build decisions.

