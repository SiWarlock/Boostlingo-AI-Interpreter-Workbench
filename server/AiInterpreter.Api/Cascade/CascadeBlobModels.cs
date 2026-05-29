using AiInterpreter.Api.Common;
using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Http;

namespace AiInterpreter.Api.Cascade;

/// <summary>
/// Per-turn parameters for the blob fallback (the service-side view of the multipart turn request). The
/// <c>Encoding</c> is the container token DERIVED from the upload content-type (never client-supplied), so
/// it routes <see cref="Providers.Deepgram.DeepgramSttProvider"/> to the REST pre-recorded path. Area-local.
/// </summary>
public sealed record CascadeBlobParams(
    string SessionId,
    string TurnId,
    LanguageDirection Direction,
    string Encoding,
    string TranslationModel,
    string TtsVoice,
    int SampleRate = 0);

/// <summary>
/// The blob run's in-memory result: the collected/persisted <see cref="InterpretationTurn"/>, the
/// concatenated target audio (RESPONSE-only — never persisted, invariant #3), its content-type, and the
/// best-effort persist outcome. Area-local; the controller maps it to <see cref="CascadeTurnResponse"/>.
/// </summary>
public sealed record CascadeBlobResult(
    InterpretationTurn Turn,
    byte[] AudioBytes,
    string AudioContentType,
    Result<string> Persist);

/// <summary>
/// The <c>POST /api/cascade/turn</c> JSON response (ARCH-009 blob shape): the assembled turn
/// (transcripts/latency/cost/status) + the target audio as base64 (the non-streaming path delivers audio
/// in-body, since it wasn't streamed) + a best-effort persistence warning. The audio is in the RESPONSE
/// ONLY — never written to the session JSON (invariant #3).
/// </summary>
public sealed record CascadeTurnResponse(
    InterpretationTurn Turn,
    string? AudioBase64,
    string? AudioContentType,
    UiError? PersistenceWarning);

/// <summary>
/// Multipart form binding for <c>POST /api/cascade/turn</c> (a bindable class — settable properties, like
/// the Options pattern, lesson §1 — to avoid the record-positional-param binding gotcha, lesson §16). The
/// <c>Audio</c> file's content-type drives both upload validation + the derived STT container encoding.
/// </summary>
public sealed class CascadeTurnForm
{
    public string? SessionId { get; set; }
    public string? TurnId { get; set; }
    public LanguageCode Source { get; set; }
    public LanguageCode Target { get; set; }
    public string? TranslationModel { get; set; }
    public string? TtsVoice { get; set; }
    public IFormFile? Audio { get; set; }
}
