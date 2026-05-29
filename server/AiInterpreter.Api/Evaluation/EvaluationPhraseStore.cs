using System.Security;
using System.Text.Json;
using AiInterpreter.Api.Common;
using AiInterpreter.Api.Sessions;

namespace AiInterpreter.Api.Evaluation;

/// <summary>
/// Loads the scripted evaluation phrases (ARCH-015) once at construction and serves lookups. A
/// self-loading instance facade over a degrade-don't-crash load (lesson §3, mirrors
/// <c>PricingLoader</c>): a missing/oversized/malformed file leaves an empty store
/// (<see cref="IsLoaded"/> false, <see cref="LoadError"/> set) rather than throwing — the evaluation
/// panel is a committed MUST but must not take down the host. Consumed by F.1.
/// </summary>
public sealed class EvaluationPhraseStore
{
    private readonly Dictionary<string, EvaluationPhrase> _byId;

    public EvaluationPhraseStore(string path)
    {
        var result = Load(path);
        if (result.IsSuccess)
        {
            Phrases = result.Value;
            IsLoaded = true;
            LoadError = null;
        }
        else
        {
            Phrases = [];
            IsLoaded = false;
            LoadError = result.Error;
        }

        _byId = new Dictionary<string, EvaluationPhrase>();
        foreach (var phrase in Phrases)
        {
            _byId[phrase.PhraseId] = phrase;
        }
    }

    public IReadOnlyList<EvaluationPhrase> Phrases { get; }

    public bool IsLoaded { get; }

    public string? LoadError { get; }

    public EvaluationPhrase? GetById(string phraseId) => _byId.GetValueOrDefault(phraseId);

    public IReadOnlyList<EvaluationPhrase> GetByLanguage(LanguageCode language) =>
        Phrases.Where(p => p.Language == language).ToList();

    /// <summary>Degrade-don't-crash load (mirrors <c>PricingLoader.Load</c>).</summary>
    public static Result<IReadOnlyList<EvaluationPhrase>> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Result<IReadOnlyList<EvaluationPhrase>>.Failure($"evaluation phrases not found: {path}");
        }

        try
        {
            const long maxBytes = 1024 * 1024; // 1 MB; the phrase file is a few KB.
            var length = new FileInfo(path).Length;
            if (length > maxBytes)
            {
                return Result<IReadOnlyList<EvaluationPhrase>>.Failure(
                    $"evaluation phrases too large ({length} bytes, max {maxBytes}): {path}");
            }

            var json = File.ReadAllText(path);
            var phrases = JsonSerializer.Deserialize<List<EvaluationPhrase>>(json, JsonDefaults.Options);

            if (phrases is null || phrases.Count == 0)
            {
                return Result<IReadOnlyList<EvaluationPhrase>>.Failure($"evaluation phrases invalid or empty: {path}");
            }

            return Result<IReadOnlyList<EvaluationPhrase>>.Success(phrases);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or SecurityException)
        {
            return Result<IReadOnlyList<EvaluationPhrase>>.Failure(
                $"evaluation phrases unreadable/invalid ({path}): {ex.Message}");
        }
    }
}
