using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Providers.Abstractions;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Tests;

// H.3-backend — SessionPersistenceReader (ARCH-016 READ tier / ARCH-019 security / lessons §11/§3/§16).
// SAFETY slice: this is the FIRST request-reachable disk-ENUMERATION + disk-DESERIALIZATION of persisted
// sessions. It is a path/DoS boundary (NOT a sanitization one — it reads already-clean data the writer
// wrote): it REUSES the §11 two-layer path guard on the read side, REUSES the §3 degrade pattern
// (pre-read size guard + filtered catch + Result<T>), and degrades PER-FILE (a corrupt/oversize/unreadable
// file is SKIPPED, never blanks the whole list). The read-side sentinel re-asserts no secret/audio survives
// a round-trip (invariants #1/#2/#3 hold structurally because the session model carries no such field).
public class SessionPersistenceReaderTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 5, 28, 15, 30, 0, TimeSpan.Zero);

    private const string SentinelApiKey = "sk-";
    private const string SentinelEphemeral = "ek_";

    private readonly List<string> _tempPaths = new();

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiw-session-reader-tests", Guid.NewGuid().ToString("N"));
        _tempPaths.Add(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }

    // A persisted session with a controllable id / StartedAt / label / turns. The rich variant (a turn
    // with a transcript + cost) makes the round-trip fidelity assertion meaningful.
    private static InterpretationSession BuildSession(
        string sessionId, DateTimeOffset startedAt, string? label = "run", bool rich = false,
        params InterpretationMode[] turnModes)
    {
        var direction = new LanguageDirection(LanguageCode.En, LanguageCode.Es);
        var profile = new ProviderProfile(
            "openai", "gpt-realtime", "deepgram", "nova-3", "multi",
            "openai", "gpt-5-nano", "openai", "gpt-4o-mini-tts", "alloy");
        var config = new SessionConfig(InterpretationMode.Cascade, direction, profile);

        var turns = new List<InterpretationTurn>();
        var modes = turnModes.Length > 0 ? turnModes
            : rich ? new[] { InterpretationMode.Cascade } : Array.Empty<InterpretationMode>();
        var i = 0;
        foreach (var mode in modes)
        {
            var transcripts = rich
                ? new List<TranscriptSegment>
                {
                    new($"seg{i}", "source", "hola mundo", true, "deepgram", startedAt, ClockSource.Server),
                }
                : new List<TranscriptSegment>();
            var cost = rich
                ? new CostEstimate("cascade", "gpt-5-nano", "composite", 0.0012m, 0.05m,
                    new Dictionary<string, decimal> { ["audioMinutes"] = 0.5m },
                    "2026-05-28-payg-estimates", new[] { "estimate only" })
                : null;
            turns.Add(new InterpretationTurn(
                $"turn{i}", mode, direction, startedAt, startedAt.AddSeconds(2), 2000,
                transcripts, new List<LatencyEvent>(), cost, null, new List<ProviderError>(),
                TurnStatus.Completed, "gpt-5-nano", "alloy"));
            i++;
        }

        return new InterpretationSession(
            sessionId, label, startedAt, startedAt.AddMinutes(1), config,
            turns, new List<ModeTransitionEvent>(), null, "2026-05-28-payg-estimates");
    }

    private static async Task WriteSession(string dir, InterpretationSession session)
    {
        var result = await new SessionPersistenceWriter(dir).WriteAsync(session);
        Assert.True(result.IsSuccess, result.Error);
    }

    // R1 — ReadAll enumerates + deserializes every valid session_*.json (round-trip fidelity via
    // JsonDefaults) AND the read-side sentinel holds: re-serializing the read result leaks no
    // sk-/ek_/raw-audio. (ARCH-016 read tier; lessons §11 read-side sentinel.)
    [Fact]
    public async Task read_all_enumerates_and_deserializes_valid_sessions()
    {
        var dir = NewTempDir();
        var rich = BuildSession("session_rich01", T.AddMinutes(2), "rich", rich: true);
        await WriteSession(dir, rich);
        await WriteSession(dir, BuildSession("session_min02", T.AddMinutes(1), "min"));
        await WriteSession(dir, BuildSession("session_min03", T, "min"));

        var result = new SessionPersistenceReader(dir).ReadAll();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(3, result.Value.Count);

        // Fidelity: the rich session re-serializes byte-identically to its original contract (lesson §2 —
        // compare serialized JSON, never record ==).
        var back = result.Value.Single(s => s.SessionId == "session_rich01");
        Assert.Equal(
            JsonSerializer.Serialize(rich, JsonDefaults.Options),
            JsonSerializer.Serialize(back, JsonDefaults.Options));
        Assert.Equal("hola mundo", back.Turns[0].Transcripts[0].Text);

        // Read-side sentinel: nothing secret/audio survives the round-trip (invariants #1/#2/#3).
        var roundTrip = JsonSerializer.Serialize(result.Value, JsonDefaults.Options);
        Assert.DoesNotContain(SentinelApiKey, roundTrip, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelEphemeral, roundTrip, StringComparison.Ordinal);
        Assert.DoesNotContain("\"audio\":", roundTrip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hola mundo", roundTrip, StringComparison.Ordinal); // not trivially empty
    }

    // R2 — degrade PER-FILE: a corrupt (non-JSON) file AND a valid-but-oversize file are SKIPPED; the
    // valid under-cap sessions still return. Proves the §3 size guard rejects on LENGTH (the oversize file
    // is valid JSON) and the filtered catch skips a parse failure — neither blanks the list (Q3). (lesson §3)
    [Fact]
    public async Task read_all_skips_corrupt_and_oversize_files_keeps_valid()
    {
        var dir = NewTempDir();
        await WriteSession(dir, BuildSession("session_ok1", T.AddMinutes(1), "ok"));
        await WriteSession(dir, BuildSession("session_ok2", T, "ok"));
        // A valid InterpretationSession but padded > the cap (label of 6000 chars) → size-guard skip,
        // proving the skip is by LENGTH not by being corrupt.
        await WriteSession(dir, BuildSession("session_big3", T.AddMinutes(2), new string('p', 6000)));
        // Non-JSON garbage matching the pattern → parse-failure skip.
        await File.WriteAllTextAsync(Path.Combine(dir, "session_corrupt.json"), "{ not valid json at all");

        var result = new SessionPersistenceReader(dir, maxBytesPerFile: 4096).ReadAll();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(2, result.Value.Count);
        Assert.All(result.Value, s => Assert.StartsWith("session_ok", s.SessionId));
    }

    // R3 — a missing data dir → an empty list (Success), never a throw or a failure. (brief: missing dir
    // is not an error — a clean clone with no sessions yet lists nothing.)
    [Fact]
    public void read_all_missing_dir_returns_empty_success()
    {
        var missing = Path.Combine(Path.GetTempPath(), "aiw-reader-missing", Guid.NewGuid().ToString("N"));

        var result = new SessionPersistenceReader(missing).ReadAll();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Empty(result.Value);
    }

    // R4 — a misconfigured data dir that resolves to a regular FILE (not a directory) → a wholesale
    // Result.Failure (deterministic + cross-platform via File.Exists), distinct from the missing-dir
    // empty-success. The controller maps this failure → a sanitized UiError (§16). (lesson §3 degrade)
    [Fact]
    public async Task read_all_file_where_dir_expected_returns_failure()
    {
        var file = Path.Combine(Path.GetTempPath(), "aiw-reader-file-" + Guid.NewGuid().ToString("N"));
        _tempPaths.Add(file);
        await File.WriteAllTextAsync(file, "x");

        var result = new SessionPersistenceReader(file).ReadAll();

        Assert.False(result.IsSuccess);
    }

    // R5 — path/pattern safety: only session_*.json directly under the data dir are read. A non-session
    // file, a non-matching .json, a session file nested in a SUBDIR (no recursion), and a sentinel file
    // OUTSIDE the dir are all ignored — the reader enumerates the data dir ONLY. (lesson §11)
    [Fact]
    public async Task read_all_reads_only_session_files_in_the_dir_no_recursion()
    {
        var dir = NewTempDir();
        await WriteSession(dir, BuildSession("session_only1", T, "only"));
        await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "not a session");
        await File.WriteAllTextAsync(Path.Combine(dir, "data.json"), "{}"); // .json but not session_*
        var sub = Path.Combine(dir, "nested");
        Directory.CreateDirectory(sub);
        await WriteSession(sub, BuildSession("session_nested9", T.AddMinutes(1), "nested")); // no recursion
        // A session file OUTSIDE the data dir entirely — must never be read.
        var outside = NewTempDir();
        await WriteSession(outside, BuildSession("session_outside8", T.AddMinutes(2), "outside"));

        var result = new SessionPersistenceReader(dir).ReadAll();

        Assert.True(result.IsSuccess, result.Error);
        var only = Assert.Single(result.Value);
        Assert.Equal("session_only1", only.SessionId);
    }

    // R-proj — SessionListItem.FromSession projects a lightweight summary: sessionId/label/startedAt/
    // endedAt + a total turnCount + the DISTINCT interpretation modes the turns used (first-seen order).
    // Pins the Q1=B payload-hygiene shape (the list carries a summary, never the full turns). A turnless
    // session → turnCount 0 + an empty modes list.
    [Fact]
    public void session_list_item_projects_summary_fields()
    {
        var session = BuildSession(
            "session_proj", T, "My run", turnModes:
            new[] { InterpretationMode.Cascade, InterpretationMode.Cascade, InterpretationMode.Realtime });

        var item = SessionListItem.FromSession(session);

        Assert.Equal("session_proj", item.SessionId);
        Assert.Equal("My run", item.Label);
        Assert.Equal(T, item.StartedAt);
        Assert.Equal(T.AddMinutes(1), item.EndedAt);
        Assert.Equal(3, item.TurnCount);
        Assert.Equal(new[] { InterpretationMode.Cascade, InterpretationMode.Realtime }, item.Modes);

        var turnless = SessionListItem.FromSession(BuildSession("session_empty", T, "empty"));
        Assert.Equal(0, turnless.TurnCount);
        Assert.Empty(turnless.Modes);
    }

    // ===== 068 — ReadById (the GET /{id} disk-fallback by-id read) =====

    // RB1 — ReadById returns the persisted session whose DESERIALIZED SessionId matches (the filename embeds
    // StartedAt, not the bare id → match by content, not filename). No matching file / a missing dir → null.
    [Fact]
    public async Task read_by_id_returns_the_matching_persisted_session()
    {
        var dir = NewTempDir();
        await WriteSession(dir, BuildSession("session_aaa", T, "a", rich: true));
        await WriteSession(dir, BuildSession("session_bbb", T.AddMinutes(1), "b"));
        await WriteSession(dir, BuildSession("session_ccc", T.AddMinutes(2), "c"));
        var reader = new SessionPersistenceReader(dir);

        Assert.Equal("session_bbb", reader.ReadById("session_bbb")!.SessionId);
        // Matched by SessionId, not filename — the rich match round-trips its content.
        Assert.Equal("hola mundo", reader.ReadById("session_aaa")!.Turns[0].Transcripts[0].Text);
        // No matching file → null; a missing dir → null (not a throw).
        Assert.Null(reader.ReadById("session_zzz"));
        Assert.Null(new SessionPersistenceReader(Path.Combine(dir, "nope")).ReadById("session_aaa"));
    }

    // RB2 — degrade: a corrupt file is SKIPPED (reuses TryReadFile); the valid match still returns, and a
    // target with only a corrupt would-be candidate → not-found (null), never a throw (Q5 / §3/§35).
    [Fact]
    public async Task read_by_id_skips_corrupt_files_and_finds_the_valid_match()
    {
        var dir = NewTempDir();
        await WriteSession(dir, BuildSession("session_good", T, "good"));
        await File.WriteAllTextAsync(Path.Combine(dir, "session_corrupt.json"), "{ not valid json");
        var reader = new SessionPersistenceReader(dir);

        Assert.Equal("session_good", reader.ReadById("session_good")!.SessionId);
        Assert.Null(reader.ReadById("session_corrupt")); // the corrupt candidate is skipped, no match
    }

    // RB3 — ⭐ the pre-FS id gate (safety rule #5 / §11): an invalid (non-allowlist) id → null WITHOUT
    // enumerating. PROVEN deterministically: a file whose deserialized SessionId IS the invalid string is
    // present + WOULD match if enumeration ran — only the pre-FS gate short-circuiting first yields null.
    [Fact]
    public async Task read_by_id_rejects_invalid_id_before_touching_the_fs()
    {
        var dir = NewTempDir();
        var evil = BuildSession("../evil", T, "evil"); // written directly — the writer would reject this id
        await File.WriteAllTextAsync(
            Path.Combine(dir, "session_evil.json"), JsonSerializer.Serialize(evil, JsonDefaults.Options));
        var reader = new SessionPersistenceReader(dir);

        Assert.Null(reader.ReadById("../evil")); // gate rejects → null despite the matching-by-id file present
        Assert.Null(reader.ReadById("a/b"));
        Assert.Null(reader.ReadById(""));
    }
}
