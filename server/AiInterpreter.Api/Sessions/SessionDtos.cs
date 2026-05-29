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
/// <c>POST /api/sessions/{id}/end</c> response (ARCH-009 Flow F). The end always succeeds in-memory
/// (<see cref="InterpretationSession.EndedAt"/> + summary set); persistence is MUST-but-reported:
/// <see cref="PersistedPath"/> on a successful write, else <see cref="PersistenceWarning"/> carries a
/// safe <c>persistence.failed</c> <see cref="UiError"/> (ARCH-018 "continue session / save warning").
/// Exactly one of the two is non-null.
/// </summary>
public sealed record EndSessionResponse(
    InterpretationSession Session,
    string? PersistedPath,
    UiError? PersistenceWarning);
