using AiInterpreter.Api.Config;
using AiInterpreter.Api.Providers.Deepgram;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiInterpreter.Tests;

// G.2b — DotEnvLoader: auto-load the repo-root .env into the process environment so a clean clone runs
// with plain `dotnet run` (no manual `set -a && source ../../.env`). Fill-gaps only (explicit env / CI /
// prod wins), degrade-if-absent, keys stay process-side. Env-var-touching tests share the serialized
// HostEnv collection (lesson §4: set-before / finally-unset / run serialized — process env is global).
[Collection("HostEnv")]
public class DotEnvLoaderTests : IDisposable
{
    private readonly List<string> _tempPaths = new();
    private readonly List<string> _envVars = new();

    private string WriteFixture(string contents)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aiw-dotenv", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        var path = Path.Combine(dir, ".env");
        File.WriteAllText(path, contents);
        return path;
    }

    private void Track(string name) => _envVars.Add(name);

    public void Dispose()
    {
        foreach (var v in _envVars) Environment.SetEnvironmentVariable(v, null);
        foreach (var p in _tempPaths)
        {
            try { if (Directory.Exists(p)) Directory.Delete(p, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }

    // 1 — Load populates the process environment from a .env fixture.
    [Fact]
    public void load_populates_env_vars_from_file()
    {
        Track("AIW_DOTENV_FOO");
        var path = WriteFixture("AIW_DOTENV_FOO=bar\n");

        DotEnvLoader.Load(path);

        Assert.Equal("bar", Environment.GetEnvironmentVariable("AIW_DOTENV_FOO"));
    }

    // 2 — Q1 fill-gaps: an already-set env var (explicit export / CI / prod) MUST win over the .env value.
    [Fact]
    public void load_does_not_override_already_set_var()
    {
        Track("AIW_DOTENV_FOO");
        Environment.SetEnvironmentVariable("AIW_DOTENV_FOO", "explicit-wins");
        var path = WriteFixture("AIW_DOTENV_FOO=from-file\n");

        DotEnvLoader.Load(path);

        Assert.Equal("explicit-wins", Environment.GetEnvironmentVariable("AIW_DOTENV_FOO"));
    }

    // 3 — §3 degrade-don't-crash: an absent .env (prod/CI) must not throw + sets nothing.
    [Fact]
    public void load_absent_file_does_not_throw()
    {
        Track("AIW_DOTENV_ABSENT");
        var missing = Path.Combine(Path.GetTempPath(), "aiw-dotenv-missing", Guid.NewGuid().ToString("N"), ".env");

        DotEnvLoader.Load(missing); // must not throw

        Assert.Null(Environment.GetEnvironmentVariable("AIW_DOTENV_ABSENT"));
    }

    // 4 — line parsing: KEY=value with trim, inline-comment strip (the 051-class guard — a model name must
    // NOT carry "# or ..."), `export` prefix, surrounding quotes, and an empty value.
    [Theory]
    [InlineData("FOO=bar", "FOO", "bar")]
    [InlineData("  FOO = bar  ", "FOO", "bar")]
    [InlineData("FOO=gpt-5-nano # or gpt-5-mini", "FOO", "gpt-5-nano")]
    [InlineData("export FOO=bar", "FOO", "bar")]
    [InlineData("FOO=\"quoted value\"", "FOO", "quoted value")]
    [InlineData("FOO=", "FOO", "")]
    public void try_parse_line_parses_assignment(string line, string expectedKey, string expectedValue)
    {
        Assert.True(DotEnvLoader.TryParseLine(line, out var key, out var value));
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedValue, value);
    }

    // 5 — non-assignment lines are skipped (blank, whitespace, full-line comment, no '=', empty key).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a full-line comment")]
    [InlineData("noequalshere")]
    [InlineData("=novalue")]
    public void try_parse_line_rejects_non_assignment(string line)
    {
        Assert.False(DotEnvLoader.TryParseLine(line, out _, out _));
    }

    // 6 — Q2 path resolution: walk up from the start dir to the nearest .env (robust to cwd / --project).
    [Fact]
    public void find_env_file_walks_up_from_start_dir()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiw-findup", Guid.NewGuid().ToString("N"));
        var deep = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        _tempPaths.Add(root);
        var envPath = Path.Combine(root, ".env");
        File.WriteAllText(envPath, "X=1\n");

        var found = DotEnvLoader.FindEnvFile(deep);

        Assert.Equal(envPath, found);
    }

    // 7 — no .env up-tree -> null (uses an impossible filename so the walk-to-root is deterministic).
    [Fact]
    public void find_env_file_returns_null_when_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiw-findup-none", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempPaths.Add(root);

        Assert.Null(DotEnvLoader.FindEnvFile(root, ".env.nonexistent-" + Guid.NewGuid().ToString("N")));
    }

    // 8 — the acceptance flow: the loader populates Environment -> the §4 env->section bridge -> a bound
    // Options value. Proves a clean `dotnet run` (loader at startup) reaches the provider config.
    [Fact]
    public void loaded_env_flows_to_bound_options()
    {
        Track("DEEPGRAM_API_KEY");
        var path = WriteFixture("DEEPGRAM_API_KEY=dg-from-dotenv\n");
        DotEnvLoader.Load(path);

        using var factory = new WebApplicationFactory<Program>();
        var deepgram = factory.Services.GetRequiredService<IOptions<DeepgramOptions>>();

        Assert.Equal("dg-from-dotenv", deepgram.Value.ApiKey);
    }

    // 9 — the test-host gate as a pure predicate (LOAD-BEARING safety): only the real API entry point
    // auto-loads; the VSTest host ("testhost"), the test assembly, an unknown entry, or null MUST skip —
    // so a dev `.env` can't flip provider DI to real under WebApplicationFactory (live calls / a
    // non-deterministic suite). Pinned DIRECTLY: in CI there is no `.env`, so proof-by-green-suite is
    // vacuous there; this guards an assembly-name typo/refactor everywhere.
    [Theory]
    [InlineData("AiInterpreter.Api", true)]
    [InlineData("testhost", false)]
    [InlineData("AiInterpreter.Tests", false)]
    [InlineData(null, false)]
    public void should_auto_load_only_for_the_api_entry_point(string? entryAssemblyName, bool expected)
    {
        Assert.Equal(expected, DotEnvLoader.ShouldAutoLoad(entryAssemblyName));
    }
}
