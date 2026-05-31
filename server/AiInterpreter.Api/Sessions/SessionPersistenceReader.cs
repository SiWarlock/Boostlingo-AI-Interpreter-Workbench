using System.Security;
using System.Text.Json;
using AiInterpreter.Api.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiInterpreter.Api.Sessions;

/// <summary>
/// H.3-backend — the persisted-session READ tier (ARCH-016): enumerates <c>SESSION_DATA_DIR</c> for
/// <c>session_*.json</c>, deserializes each via the shared <see cref="JsonDefaults"/>, and returns the
/// sessions for the <c>GET /api/sessions</c> history list. The FIRST request-reachable disk-ENUMERATION +
/// DESERIALIZATION of persisted sessions (the <see cref="SessionPersistenceWriter"/> was write-only),
/// so the H.3 history view can list past sessions even across a server restart (the in-memory store is
/// empty then).
///
/// <b>A path/DoS boundary, NOT a sanitization one</b> (it reads already-clean data the writer wrote —
/// the session model carries no key/secret/audio field):
/// <list type="bullet">
/// <item>Reuses the lesson §3 degrade pattern — a pre-read size guard + a filtered catch + <c>Result&lt;T&gt;</c>.</item>
/// <item>Degrades PER-FILE: a corrupt / oversize / unreadable session file is SKIPPED (logged single-lined,
///   §13), never blanking the whole list — one bad file can't take down the history (Q3).</item>
/// <item>Enumerates the data dir ONLY (<see cref="SearchOption.TopDirectoryOnly"/> + the
///   <c>session_*.json</c> pattern) — no recursion; a non-matching or nested file is ignored. The
///   enumerated paths are inherently under the data dir, so no per-path canonicalization is needed on the
///   read side (no input-derived path, no recursion → no escape; the §11 guard is a WRITE-side concern).</item>
/// <item>A missing dir → an empty list (Success); a <c>dataDir</c> that resolves to a regular FILE (a
///   misconfigured <c>SESSION_DATA_DIR</c>) → a wholesale <c>Result.Failure</c> the controller maps to a
///   sanitized <c>sessions.read_failed</c> <c>UiError</c> (§16) — distinct from the empty-but-fine case.</item>
/// </list>
///
/// <b>Symlink note (security boundary):</b> a <c>session_*.json</c> symlink whose target resolves outside
/// the data dir would be followed on read (<c>Path.GetFullPath</c> does not resolve link targets). For the
/// single-user-local threat model the writer owns the data dir (no attacker-planted symlinks) and a symlink
/// to a non-session payload is parse/size-skipped, so this is documented OUT-OF-THREAT-MODEL (ARCH-019).
/// </summary>
public sealed class SessionPersistenceReader
{
    /// <summary>Per-file read cap (DoS guard). 8 MB — a session JSON (many turns + latency events) can
    /// exceed pricing.json's 1 MB; override via <c>SESSION_MAX_READ_BYTES</c>.</summary>
    public const long DefaultMaxReadBytes = 8L * 1024 * 1024;

    private readonly string _dataDir;
    private readonly long _maxBytesPerFile;
    private readonly ILogger<SessionPersistenceReader> _logger;

    public SessionPersistenceReader(
        string dataDir, long maxBytesPerFile = DefaultMaxReadBytes, ILogger<SessionPersistenceReader>? logger = null)
    {
        _dataDir = dataDir;
        _maxBytesPerFile = maxBytesPerFile;
        _logger = logger ?? NullLogger<SessionPersistenceReader>.Instance;
    }

    /// <summary>
    /// Reads every valid persisted session under the data dir (the most-recent-first ordering + the
    /// summary projection are the service's concern). Missing dir → empty Success; a file-where-dir →
    /// Failure; per-file errors are skipped. Never throws on a per-file or missing-dir condition.
    /// </summary>
    public Result<IReadOnlyList<InterpretationSession>> ReadAll()
    {
        // A misconfigured SESSION_DATA_DIR pointing at a regular file is a real (degraded) failure, distinct
        // from a genuinely-absent dir (an empty, not-yet-used history) which is a clean empty list.
        if (!Directory.Exists(_dataDir))
        {
            return File.Exists(_dataDir)
                ? Result<IReadOnlyList<InterpretationSession>>.Failure(
                    $"session data dir is a file, not a directory: {_dataDir}")
                : Result<IReadOnlyList<InterpretationSession>>.Success(Array.Empty<InterpretationSession>());
        }

        try
        {
            var sessions = new List<InterpretationSession>();
            // TopDirectoryOnly + the session_*.json pattern: dir-scoped, no recursion (lesson §11 — read side).
            foreach (var path in Directory.EnumerateFiles(_dataDir, "session_*.json", SearchOption.TopDirectoryOnly))
            {
                var session = TryReadFile(path);
                if (session is not null)
                {
                    sessions.Add(session);
                }
            }

            return Result<IReadOnlyList<InterpretationSession>>.Success(sessions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or SecurityException)
        {
            // A dir-level enumeration failure degrades wholesale (lesson §3); the detail stays server-side.
            return Result<IReadOnlyList<InterpretationSession>>.Failure(
                $"session history unreadable ({_dataDir}): {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the persisted session whose <c>SessionId</c> matches <paramref name="id"/> (068 — the GET /{id}
    /// disk fallback). The writer filename embeds <c>StartedAt</c>, not the bare id, so this ENUMERATES
    /// <c>session_*.json</c> and matches on the DESERIALIZED <c>SessionId</c>, short-circuiting on the first
    /// match. Degrades to null (not-found) — never throws — on an invalid id (rejected PRE-FS, safety rule
    /// #5), a missing dir, a corrupt/oversize would-be candidate (skipped per-file), or a dir-misconfig
    /// (logged; the LIST path is the loud 500 surface for that). The service tries the in-memory store FIRST,
    /// this disk read SECOND (in-memory wins).
    /// </summary>
    public InterpretationSession? ReadById(string id)
    {
        // Pre-FS id gate — reuse the §11 write-side allowlist (^[A-Za-z0-9_-]+$): an invalid id (../, a
        // non-allowlist char, empty) is "not found" WITHOUT touching the FS — defense-in-depth (safety rule
        // #5), load-bearing if a future optimization ever narrows the scan by a filename fragment built from id.
        if (!SessionPersistenceWriter.IsValidSessionId(id))
        {
            return null;
        }

        if (!Directory.Exists(_dataDir))
        {
            if (File.Exists(_dataDir))
            {
                // A misconfigured SESSION_DATA_DIR (a file, not a dir): logged here (the LIST endpoint is the
                // loud 500 surface); a by-id lookup degrades to not-found — no new failure channel on GET /{id}.
                _logger.LogWarning("Session data dir is a file, not a directory: {Dir}", _dataDir);
            }

            return null;
        }

        try
        {
            // TopDirectoryOnly + the session_*.json pattern: dir-scoped, no recursion (lesson §11 — read side).
            foreach (var path in Directory.EnumerateFiles(_dataDir, "session_*.json", SearchOption.TopDirectoryOnly))
            {
                // Match on the deserialized SessionId (not the filename). A corrupt/oversize candidate is
                // skipped by TryReadFile (null) and simply doesn't match → a corrupt-only would-be match is
                // not-found (Q5), never a throw.
                var session = TryReadFile(path);
                if (session is not null && session.SessionId == id)
                {
                    return session;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or SecurityException)
        {
            // A dir-level enumeration failure degrades to not-found (lesson §3); detail stays server-side.
            _logger.LogWarning("Session by-id read failed ({Dir}): {Error}",
                _dataDir, ex.Message.ReplaceLineEndings(" "));
            return null;
        }
    }

    // Reads + deserializes one session file, degrading to null (SKIP) on a size-cap breach, a corrupt
    // payload, or an IO error — so one bad file never blanks the list (Q3 / lesson §3).
    private InterpretationSession? TryReadFile(string path)
    {
        try
        {
            // Pre-read size guard (lesson §3): reject an oversized file BEFORE reading it. Open ONCE and
            // size-check the live handle, then deserialize from the SAME stream — closing the stat→read
            // TOCTOU that a separate FileInfo.Length + File.ReadAllText would leave (the cap is a hard
            // pre-read cap on this open handle, not a snapshot a swap could slip past).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > _maxBytesPerFile)
            {
                _logger.LogWarning("Skipping oversize session file {File} ({Length} bytes, max {Max})",
                    Path.GetFileName(path), stream.Length, _maxBytesPerFile);
                return null;
            }

            var session = JsonSerializer.Deserialize<InterpretationSession>(stream, JsonDefaults.Options);
            if (session is null || string.IsNullOrEmpty(session.SessionId))
            {
                _logger.LogWarning("Skipping invalid/empty session file {File}", Path.GetFileName(path));
                return null;
            }

            return session;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or SecurityException)
        {
            // Single-lined (§13) so a corrupt-file message can't forge log lines; filename only (no content).
            _logger.LogWarning("Skipping unreadable session file {File}: {Error}",
                Path.GetFileName(path), ex.Message.ReplaceLineEndings(" "));
            return null;
        }
    }
}
