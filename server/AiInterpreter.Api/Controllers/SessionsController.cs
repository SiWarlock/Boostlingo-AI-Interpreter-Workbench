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
    public ActionResult<InterpretationSession> Create([FromBody] CreateSessionRequest request)
        => Ok(_sessions.Create(request));

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

        // The end always happened in-memory; report persistence per ARCH-016 Flow F (path on success,
        // a safe persistence.failed warning otherwise) — never a 500, never serializing the Result.
        var response = outcome.Persist.IsSuccess
            // Expose the filename only, never the absolute server path (no data-dir/username disclosure
            // — ARCH-019); the user knows SESSION_DATA_DIR from their config, so filename = findable.
            ? new EndSessionResponse(outcome.Session, Path.GetFileName(outcome.Persist.Value), PersistenceWarning: null)
            : new EndSessionResponse(outcome.Session, PersistedPath: null,
                _sanitizer.SanitizeResult("persistence.failed", outcome.Persist));

        return Ok(response);
    }

    [HttpGet("{id}/summary")]
    public ActionResult<SessionSummary> Summary(string id)
    {
        var summary = _sessions.Summary(id);
        return summary is null ? NotFoundUiError() : Ok(summary);
    }

    // Unknown routed resource -> a sanitized 404 UiError (distinct from the framework 404/405 for
    // UNrouted paths, which stay ProblemDetails per the B.9a decision).
    private ObjectResult NotFoundUiError() =>
        StatusCode(StatusCodes.Status404NotFound, _sanitizer.ForCode("session.not_found", StatusCodes.Status404NotFound));
}
