using System.Globalization;
using System.Security;
using System.Text.Json;
using AiInterpreter.Api.Common;

namespace AiInterpreter.Api.Sessions;

/// <summary>
/// Serializes an <see cref="InterpretationSession"/> to local JSON under <c>SESSION_DATA_DIR</c> via
/// the shared <see cref="JsonDefaults"/> contract (ARCH-016) — the same camelCase contract the HTTP
/// pipeline uses, so API and persisted JSON cannot diverge.
///
/// Two-layer path-traversal guard (ARCH-019 §9 / root CLAUDE.md safety rule #5): (a) the session id
/// must match <c>^[A-Za-z0-9_-]+$</c> (checked before touching the filesystem); (b) the canonicalized
/// resolved path must stay under <c>SESSION_DATA_DIR</c> (defense-in-depth). Degrades — never throws
/// — on an IO failure (ARCH-018, lesson §3): returns <c>Result&lt;string&gt;.Failure("persistence.failed: …")</c>
/// whose <c>[JsonIgnore]</c> Error stays server-side. A caller cancellation propagates (not degraded).
///
/// Filename <c>session_&lt;StartedAt UTC yyyyMMddTHHmmssZ&gt;_&lt;short-id&gt;.json</c> derives from the session's
/// StartedAt (stamped by IClock at create), so the best-effort per-turn writes and the write-on-end
/// overwrite ONE file per session. The optional label is a JSON field only — never in the filename.
///
/// This writer is NOT a sanitization boundary (that is B.8's ErrorSanitizer / ARCH-008): it serializes
/// the model as-is. The never-persist guarantee (no standard key / no ephemeral <c>ek_</c> secret / no
/// raw audio — safety rules #1/#2/#3) holds because the session model carries no such field; the
/// SessionPersistenceTests sentinel makes that explicit and drift-proof.
/// </summary>
public sealed class SessionPersistenceWriter
{
    private const string FailCode = "persistence.failed";

    private readonly string _dataDir;

    public SessionPersistenceWriter(string dataDir) => _dataDir = dataDir;

    /// <summary>
    /// Writes the session JSON and returns the resolved full path on success, or a degraded
    /// <c>persistence.failed</c> failure on a rejected id / out-of-root path / IO error.
    /// </summary>
    public async Task<Result<string>> WriteAsync(InterpretationSession session, CancellationToken ct = default)
    {
        // Layer (a): reject any id outside the server-generated allowlist BEFORE touching the FS.
        if (!IsValidSessionId(session.SessionId))
        {
            return Result<string>.Failure($"{FailCode}: invalid sessionId '{session.SessionId}'");
        }

        try
        {
            var dirFull = Path.GetFullPath(_dataDir);
            var fileFull = Path.GetFullPath(Path.Combine(dirFull, BuildFileName(session)));

            // Layer (b): defense-in-depth — the canonicalized path must stay under the data dir.
            var dirPrefix = dirFull.EndsWith(Path.DirectorySeparatorChar)
                ? dirFull
                : dirFull + Path.DirectorySeparatorChar;
            if (!fileFull.StartsWith(dirPrefix, StringComparison.Ordinal))
            {
                return Result<string>.Failure($"{FailCode}: resolved path escapes data dir");
            }

            Directory.CreateDirectory(dirFull);
            var json = JsonSerializer.Serialize(session, JsonDefaults.Options);
            await File.WriteAllTextAsync(fileFull, json, ct);

            return Result<string>.Success(fileFull);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or SecurityException)
        {
            // ARCH-018 degrade-don't-crash; original detail stays server-side ([JsonIgnore] Error).
            return Result<string>.Failure($"{FailCode}: {ex.Message}");
        }
    }

    private static string BuildFileName(InterpretationSession session)
    {
        var timestamp = session.StartedAt.UtcDateTime
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortId = session.SessionId.StartsWith("session_", StringComparison.Ordinal)
            ? session.SessionId["session_".Length..]
            : session.SessionId;
        return $"session_{timestamp}_{shortId}.json";
    }

    // Matches ^[A-Za-z0-9_-]+$ (non-empty): the server-generated id allowlist (ARCH-016 / ARCH-019).
    private static bool IsValidSessionId(string id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
