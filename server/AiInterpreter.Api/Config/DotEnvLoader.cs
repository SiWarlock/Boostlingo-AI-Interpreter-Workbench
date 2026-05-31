using System.Reflection;

namespace AiInterpreter.Api.Config;

/// <summary>
/// Auto-loads the repo-root <c>.env</c> into the process environment at startup (G.2b) so a clean clone
/// runs with plain <c>dotnet run</c> — removing the manual <c>set -a &amp;&amp; source ../../.env</c> ritual.
/// The existing Program.cs env→section bridge (server lesson §4) then binds the Options from these vars.
///
/// Three deliberate properties (ARCH-028 / ARCH-029):
/// <list type="bullet">
/// <item><b>Fill-gaps, never override</b> — a value already in the environment (an explicit <c>export</c>,
/// CI, or prod) WINS; <c>.env</c> is only the fallback for absent keys (Q1).</item>
/// <item><b>Degrade-don't-crash</b> (lesson §3) — an absent <c>.env</c> (prod/CI) is a no-op, never a throw.</item>
/// <item><b>Gated to the real API entry point</b> (<see cref="ShouldAutoLoad"/>) — the loader must NOT run
/// under the test host: the dev <c>.env</c> holds real provider keys, and loading them into a
/// <c>WebApplicationFactory</c> host would flip provider DI from fakes to real (live calls / a
/// non-deterministic suite, ARCH-020).</item>
/// </list>
/// Standard keys stay process-side (invariant #1) — the loader never logs or echoes a value.
/// </summary>
public static class DotEnvLoader
{
    /// <summary>
    /// Startup entry point: if the caller is the real API process, walk up from <paramref name="startDir"/>
    /// to the nearest <c>.env</c> and load it. Glue over <see cref="ShouldAutoLoad"/> + <see cref="FindEnvFile"/>
    /// + <see cref="Load"/>; safe to call unconditionally (a no-op off the API entry point or with no
    /// <c>.env</c> up-tree).
    /// </summary>
    public static void AutoLoad(string startDir)
    {
        if (!ShouldAutoLoad(Assembly.GetEntryAssembly()?.GetName().Name))
        {
            return;
        }

        var path = FindEnvFile(startDir);
        if (path is not null)
        {
            Load(path);
        }
    }

    /// <summary>
    /// The test-host gate: auto-load ONLY when the process entry assembly is the API itself. The VSTest host
    /// (<c>testhost</c>), the test assembly, or a null entry (the conservative default) all SKIP — so a dev
    /// <c>.env</c> can't leak its real keys into a <c>WebApplicationFactory</c> host. Pure + total.
    /// </summary>
    public static bool ShouldAutoLoad(string? entryAssemblyName) => entryAssemblyName == "AiInterpreter.Api";

    /// <summary>
    /// Walks up from <paramref name="startDir"/> (the assembly bin dir at startup — robust to the <c>dotnet
    /// run</c> cwd) returning the first ancestor's <paramref name="fileName"/> that exists, or null.
    /// </summary>
    public static string? FindEnvFile(string startDir, string fileName = ".env")
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Loads <paramref name="path"/> into the process environment, FILL-GAPS only (an already-set var wins).
    /// An absent file is a no-op (degrade-don't-crash, §3). Blank/comment lines + inline comments are skipped
    /// (<see cref="TryParseLine"/>).
    /// </summary>
    public static void Load(string path)
    {
        if (!File.Exists(path))
        {
            return; // degrade-don't-crash: prod/CI has no .env (§3)
        }

        foreach (var raw in File.ReadLines(path))
        {
            if (!TryParseLine(raw, out var key, out var value))
            {
                continue;
            }

            // Fill-gaps: an explicit env / CI / prod value WINS; only set an absent key (Q1, no override).
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>
    /// Parses one <c>.env</c> line into <paramref name="key"/>/<paramref name="value"/>. Returns false for a
    /// blank line, a full-line <c>#</c> comment, or a line without a key before <c>=</c>. Strips an optional
    /// leading <c>export</c>, surrounding matched quotes (which protect a literal <c>#</c>), and an UNQUOTED
    /// trailing inline comment (whitespace + <c>#</c>) — so <c>MODEL=gpt-5-nano # or gpt-5-mini</c> yields
    /// <c>gpt-5-nano</c>, not the comment-bearing string (a 051-class model-name bug otherwise).
    /// </summary>
    internal static bool TryParseLine(string raw, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var line = raw.Trim();
        if (line.Length == 0 || line[0] == '#')
        {
            return false; // blank or full-line comment
        }

        if (line.StartsWith("export ", StringComparison.Ordinal))
        {
            line = line[7..].TrimStart();
        }

        var eq = line.IndexOf('=');
        if (eq <= 0)
        {
            return false; // no '=' or an empty key
        }

        key = line[..eq].Trim();
        if (key.Length == 0)
        {
            return false;
        }

        value = Unquote(line[(eq + 1)..].Trim());
        return true;
    }

    // Surrounding matched quotes -> inner verbatim (the quotes protect a literal '#'); otherwise strip an
    // unquoted trailing inline comment (whitespace + '#') and trim the remainder.
    private static string Unquote(string rest)
    {
        if (rest.Length >= 2 && (rest[0] == '"' || rest[0] == '\'') && rest[^1] == rest[0])
        {
            return rest[1..^1];
        }

        for (var i = 1; i < rest.Length; i++)
        {
            if (rest[i] == '#' && char.IsWhiteSpace(rest[i - 1]))
            {
                return rest[..i].TrimEnd();
            }
        }

        return rest;
    }
}
