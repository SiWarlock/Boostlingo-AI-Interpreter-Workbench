using System.ComponentModel.DataAnnotations;
using AiInterpreter.Api.Security;

namespace AiInterpreter.Api.Sessions;

/// <summary>
/// <c>POST /api/sessions</c> request (ARCH-009). The server assembles the full <c>ProviderProfile</c>
/// from these request models + the A.2 Options; the client supplies only the selectable models.
/// The <see cref="MaxLengthAttribute"/> caps bound the external input at the boundary (ARCH-019):
/// an oversized string is auto-rejected (400) before it can inflate the in-memory store / persisted
/// JSON. (Validating models against the published catalog is a flagged follow-up.)
/// </summary>
public sealed record CreateSessionRequest(
    [MaxLength(512)] string? Label,
    InterpretationMode Mode,
    LanguageDirection Direction,
    [Required, MaxLength(256)] string RealtimeModel,
    [Required, MaxLength(256)] string TranslationModel);

/// <summary>
/// <c>POST /api/sessions/{id}/mode</c> request (ARCH-009 / Flow G, 050). The TARGET interpretation mode
/// as the wire string (<c>"cascade"</c>|<c>"realtime"</c>, the shape the frontend ships). Typed as a raw
/// <see cref="string"/> — NOT the <see cref="InterpretationMode"/> enum — so an off-enum value is rejected
/// as a sanitized <c>400 session.invalid_mode</c> <see cref="UiError"/> at the service chokepoint
/// (<see cref="SessionService"/>) rather than a framework ProblemDetails deserialization error
/// (lesson §27 pattern). Capped at the boundary (ARCH-019); the service validates against the enum
/// allowlist (<c>Enum.TryParse</c> + <c>Enum.IsDefined</c>).
/// </summary>
public sealed record SetModeRequest([MaxLength(32)] string? Mode);

/// <summary>
/// <c>POST /api/sessions/{id}/end</c> response (ARCH-009 Flow F). The end always succeeds in-memory
/// (<see cref="InterpretationSession.EndedAt"/> + summary set); persistence is MUST-but-reported:
/// <see cref="PersistedPath"/> (filename only — no absolute-path disclosure, ARCH-019) on a successful
/// write, else <see cref="PersistenceWarning"/> carries a safe <c>persistence.failed</c>
/// <see cref="UiError"/> (ARCH-018 "continue session / save warning"). Exactly one is non-null.
/// </summary>
public sealed record EndSessionResponse(
    InterpretationSession Session,
    string? PersistedPath,
    UiError? PersistenceWarning);

// --- Turn lifecycle (B.9c-ii). Backend owns turnId. ---

/// <summary><c>POST /api/sessions/{id}/turns</c> response (ARCH-009): the backend-generated turn id.</summary>
public sealed record CreateTurnResponse(string TurnId);

/// <summary>
/// <c>POST …/turns/{turnId}/events</c> request (ARCH-009): a batch of normalized <see cref="LatencyEvent"/>s
/// reported by the Realtime client (E.4). Capped at the boundary (ARCH-019) — oversized → 400.
/// </summary>
public sealed record AppendEventsRequest(
    [Required, MaxLength(500)] List<LatencyEvent> Events);

/// <summary>
/// <c>POST …/turns/{turnId}/complete</c> request (B.9c-ii realizes this — ARCH-009 left it
/// underspecified). <c>/complete</c> is the REALTIME finalize path (cascade turns are persisted by
/// C.4's WS, never here): the client reports the audio duration, the source/target transcripts it
/// rendered, and the final status. Cost/WER/translation-model/tts-voice are deliberately NOT here —
/// cost + WER are backend-owned (ARCH-014 / ARCH-005 inv. #5), and model/voice are cascade fields.
/// All optional; merged into the turn that already holds its <c>/events</c> latency. <c>Status</c> is
/// coerced to a terminal value: <c>Failed</c> if explicitly reported, otherwise <c>Completed</c> (a
/// non-terminal value can't drag the turn backwards) — so <c>null</c> ⇒ <c>Completed</c>.
/// <c>OutputAudioDurationMs</c> (E.2b, optional) is the realtime OUTPUT audio the client played, used to
/// price the output side of the turn's realtime cost (E.4 reports it); absent → output cost is disclosed-
/// unavailable in the estimate's <c>Assumptions</c>, never silently 0 (ARCH-014 / streaming-honesty).
/// The <c>*AudioTokens</c> fields (053-C2a) carry the realtime turn's EXACT audio-token counts from the
/// DC's <c>response.done.usage</c> (input/output <c>token_details.audio_tokens</c> + input
/// <c>cached_tokens</c>); present ⇒ the realtime cost prices from them exactly (no audio-seconds × factor
/// estimate), absent ⇒ the seconds estimate. Trailing-optional → existing positional callers unaffected.
/// Text tokens are deliberately not carried (disclosed-unpriced — no text rates configured). TS mirror = 053-C2b.
/// </summary>
public sealed record CompleteTurnRequest(
    long? AudioDurationMs,
    [MaxLength(500)] List<TranscriptSegment>? Transcripts,
    TurnStatus? Status,
    [Range(0, long.MaxValue)] long? OutputAudioDurationMs = null, // non-negative ms; null ⇒ output cost disclosed-unavailable
    [Range(0, int.MaxValue)] int? InputAudioTokens = null,        // 053-C2a realtime exact audio-token counts
    [Range(0, int.MaxValue)] int? OutputAudioTokens = null,       // (response.done.usage); present ⇒ exact-count
    [Range(0, int.MaxValue)] int? CachedAudioInputTokens = null); // pricing, absent ⇒ seconds estimate

/// <summary>
/// <c>POST …/turns/{turnId}/complete</c> response. The turn is always Completed in-memory; the per-turn
/// write is best-effort (ARCH-016) — a write failure surfaces a safe <c>persistence.failed</c>
/// <see cref="UiError"/> in <see cref="PersistenceWarning"/> (200, not 500).
/// </summary>
public sealed record CompleteTurnResponse(
    InterpretationTurn Turn,
    UiError? PersistenceWarning);
