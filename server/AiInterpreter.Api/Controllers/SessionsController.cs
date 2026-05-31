using AiInterpreter.Api.Security;
using AiInterpreter.Api.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiInterpreter.Api.Controllers;

/// <summary>
/// Session-lifecycle routes (ARCH-009). Thin (ARCH-008): orchestration lives in
/// <see cref="ISessionService"/>. The controller owns the HTTP boundary — mapping an unknown id to a
/// sanitized 404 <see cref="UiError"/> (<c>session.not_found</c>) and the <c>/end</c> persistence
/// <c>Result&lt;string&gt;</c> to a path or a safe warning (never serializing a <c>Result</c>).
/// </summary>
[ApiController]
[Route("api/sessions")]
public sealed class SessionsController : ControllerBase
{
    private readonly ISessionService _sessions;
    private readonly ErrorSanitizer _sanitizer;

    public SessionsController(ISessionService sessions, ErrorSanitizer sanitizer)
    {
        _sessions = sessions;
        _sanitizer = sanitizer;
    }

    [HttpPost]
    public async Task<ActionResult<InterpretationSession>> Create([FromBody] CreateSessionRequest request)
        => Ok(await _sessions.CreateAsync(request));

    [HttpGet("{id}")]
    public ActionResult<InterpretationSession> Get(string id)
    {
        var session = _sessions.Get(id);
        return session is null ? NotFoundUiError() : Ok(session);
    }

    [HttpPost("{id}/end")]
    public async Task<ActionResult<EndSessionResponse>> End(string id, CancellationToken cancellationToken)
    {
        var outcome = await _sessions.EndAsync(id, cancellationToken);
        if (outcome is null)
        {
            return NotFoundUiError();
        }

        // The end always happened in-memory; report persistence per ARCH-016 Flow F — path (filename
        // only) on success, a safe persistence.failed warning otherwise; never a 500, never serializing
        // the Result (the helper is the shared /end + /complete persistence-outcome mapping).
        var (persistedPath, warning) = outcome.Persist.ToPersistenceOutcome(_sanitizer);
        return Ok(new EndSessionResponse(outcome.Session, persistedPath, warning));
    }

    // Flow-G mode switch (050 / Finding 2c): validate + update the session's CurrentMode and record a
    // ModeTransitionEvent. Off-enum target -> sanitized 400 session.invalid_mode (NOT a framework
    // ProblemDetails — the DTO carries the raw string so this chokepoint owns the rejection, lesson §27).
    [HttpPost("{id}/mode")]
    public ActionResult<InterpretationSession> SwitchMode(string id, [FromBody] SetModeRequest request)
    {
        var outcome = _sessions.SwitchMode(id, request.Mode);
        return outcome.Status switch
        {
            SwitchModeStatus.NotFound => NotFoundUiError(),
            SwitchModeStatus.InvalidMode => StatusCode(
                StatusCodes.Status400BadRequest,
                _sanitizer.ForCode("session.invalid_mode", StatusCodes.Status400BadRequest)),
            _ => Ok(outcome.Session),
        };
    }

    [HttpGet("{id}/summary")]
    public ActionResult<SessionSummary> Summary(string id)
    {
        var summary = _sessions.Summary(id);
        return summary is null ? NotFoundUiError() : Ok(summary);
    }

    // --- Turn lifecycle (B.9c-ii). Backend owns turnId. ---

    [HttpPost("{id}/turns")]
    public ActionResult<CreateTurnResponse> CreateTurn(string id)
    {
        var turnId = _sessions.CreateTurn(id);
        return turnId is null ? NotFoundUiError() : Ok(new CreateTurnResponse(turnId));
    }

    [HttpPost("{id}/turns/{turnId}/events")]
    public ActionResult<InterpretationTurn> AppendEvents(string id, string turnId, [FromBody] AppendEventsRequest request)
    {
        if (_sessions.Get(id) is null)
        {
            return NotFoundUiError();
        }

        var turn = _sessions.AppendEvents(id, turnId, request.Events);
        return turn is null ? NotFoundUiError("turn.not_found") : Ok(turn);
    }

    [HttpPost("{id}/turns/{turnId}/complete")]
    public async Task<ActionResult<CompleteTurnResponse>> CompleteTurn(
        string id, string turnId, [FromBody] CompleteTurnRequest request, CancellationToken cancellationToken)
    {
        if (_sessions.Get(id) is null)
        {
            return NotFoundUiError();
        }

        var outcome = await _sessions.CompleteTurnAsync(id, turnId, request, cancellationToken);
        if (outcome is null)
        {
            return NotFoundUiError("turn.not_found");
        }

        // Per-turn persist is best-effort (ARCH-016): the turn is always Completed in-memory; report a
        // save warning if the write failed (200, never 500). The path isn't surfaced here (Flow F's
        // path is /end's); only the warning matters for the best-effort per-turn write.
        var (_, warning) = outcome.Persist.ToPersistenceOutcome(_sanitizer);
        return Ok(new CompleteTurnResponse(outcome.Turn, warning));
    }

    // Unknown routed resource -> a sanitized 404 UiError (distinct from the framework 404/405 for
    // UNrouted paths, which stay ProblemDetails per the B.9a decision).
    private ObjectResult NotFoundUiError(string code = "session.not_found") =>
        StatusCode(StatusCodes.Status404NotFound, _sanitizer.ForCode(code, StatusCodes.Status404NotFound));
}
